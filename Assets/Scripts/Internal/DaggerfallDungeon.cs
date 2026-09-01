// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

//#define SHOW_LAYOUT_TIMES

using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using Unity.Profiling;
using Mirror;
using System.Linq;
using System.Reflection;

namespace DaggerfallWorkshop
{
    public class DaggerfallDungeon : NetworkBehaviour
    {
		
		    [SyncVar] 
   public float PositionY; // ✅ This ensures all clients get the exact Y from the host

        // Server-authored context for enemies imported by this dungeon.
        // Dungeon enemies use these DF X/Z coordinates as their world anchor instead of
        // converting artificial underground Unity X/Z offsets into DF distance.
        [SyncVar] public uint RequesterNetId;
        [SyncVar] public bool HasDungeonWorldAnchor;
        [SyncVar] public int DungeonAnchorWorldX;
        [SyncVar] public int DungeonAnchorWorldZ;

        // Stable host-generated identity for network dungeon save/reload validation.
        // Do not use Mirror netId for saves; netIds are runtime-only and can be reused.
        [SyncVar] public bool IsNetworkDungeonInstance;
        [SyncVar] public string DungeonInstanceId = string.Empty;

        // Used only when an MP dungeon save is loaded in singleplayer.
        // Allows a temporary local recovery dungeon to stay at its saved MP Y slot.
        public bool IsRecoveredNetworkDungeonSave;

        public bool isSet = false;
        DaggerfallUnity dfUnity;


		// Dungeon texture swaps
        public DungeonTextureUse DungeonTextureUse = DungeonTextureUse.UseLocation_PartiallyImplemented;
        public int[] DungeonTextureTable = new int[] { 119, 120, 122, 123, 124, 168 };

        // Random monsters
        public int RandomMonsterVariance = 4;

        // Network dungeon generation authority.
        // When a client requests a dungeon, that requester supplies the generation spec used by the host.
        // The host then relays this same spec to every other client so textures and local-only dungeon layout match.
        public bool HasAuthoritativeGenerationSpec = false;
        public int AuthoritativeRequesterLevel = 0;
        public int AuthoritativeMonsterSeed = 0;

        // Optional action-state snapshot supplied only by the player who causes this
        // network dungeon to be created from a save/current SP dungeon. The server keeps
        // this on the dungeon and includes it in all later DungeonNetworkData so late
        // clients build the same authoritative starting state. Requests for an already
        // existing dungeon never replace this value.
        string initialSavedActionState = string.Empty;
        bool initialSavedActionStateApplied = false;
        bool initialSavedActionStateApplyScheduled = false;

        const int InitialSavedActionStateVersion = 1;
        const int MaxInitialSavedActionStateEntries = 16384;
        const int MaxInitialSavedActionStateEncodedLength = 1024 * 1024;

        public bool InitialSavedActionStateReady
        {
            get { return string.IsNullOrEmpty(initialSavedActionState) || initialSavedActionStateApplied; }
        }

        GameObject startMarker = null;
        GameObject enterMarker = null;
        List<Vector3> debuggerMarkerPositions = null;

        static readonly ProfilerMarker s_LayoutDungeonMarker = new ProfilerMarker("Daggerfall.LayoutDungeon");
        static readonly BindingFlags playerGPSPrivateInstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo playerGPSCurrentLocationField = typeof(PlayerGPS).GetField("currentLocation", playerGPSPrivateInstanceFlags);
        static readonly FieldInfo playerGPSHasCurrentLocationField = typeof(PlayerGPS).GetField("hasCurrentLocation", playerGPSPrivateInstanceFlags);
        static readonly FieldInfo playerGPSCurrentClimateIndexField = typeof(PlayerGPS).GetField("currentClimateIndex", playerGPSPrivateInstanceFlags);

        struct PlayerGPSLocationOverrideState
        {
            public bool Applied;
            public PlayerGPS GPS;
            public DFLocation PreviousLocation;
            public bool PreviousHasCurrentLocation;
        }


        // Host-authoritative vertical placement for networked dungeons.
        // Slot 1 starts at -500Y, then each additional active dungeon is placed 300 units lower:
        // -500, -800, -1100, ...
        public const float NetworkDungeonFirstY = -500f;
        public const float NetworkDungeonYSpacing = -300f;

        /// <summary>
        /// Server-side allocator for networked dungeon Y slots.
        /// Do not use FindObjectsOfType().Length to assign Y. That reuses occupied slots when a dungeon is destroyed.
        /// This scans the host's currently active/generated dungeon objects and returns the first free vertical slot.
        /// </summary>
        public static float GetNextAvailableDungeonY(DaggerfallDungeon ignore = null)
        {
            float firstAbs = Mathf.Abs(NetworkDungeonFirstY);
            if (firstAbs < 1f)
                firstAbs = 500f;

            float spacing = Mathf.Abs(NetworkDungeonYSpacing);
            if (spacing < 1f)
                spacing = 300f;

            HashSet<int> usedSlots = new HashSet<int>();

            foreach (DaggerfallDungeon dungeon in FindObjectsOfType<DaggerfallDungeon>())
            {
                if (dungeon == null || dungeon == ignore)
                    continue;

                // PositionY is assigned immediately when the host creates a dungeon, so include in-progress dungeons too.
                // Skip unassigned/default objects at/near 0 so the prefab/new object itself does not reserve a slot.
                float absY = Mathf.Abs(dungeon.PositionY);
                if (absY < 1f)
                    continue;

                // Convert existing dungeon Y back into our slot number. Slot 1 is NetworkDungeonFirstY.
                // This also tolerates old saves/live objects whose Y is not exactly on the new grid.
                int slot = Mathf.RoundToInt((absY - firstAbs) / spacing) + 1;
                if (slot >= 1)
                    usedSlots.Add(slot);
            }

            int nextSlot = 1;
            while (usedSlots.Contains(nextSlot))
                nextSlot++;

            float y = -firstAbs - spacing * (nextSlot - 1);
            Debug.Log($"[DungeonYAllocator] Assigned free dungeon slot {nextSlot} at Y={y}. firstY={-firstAbs}, spacing={spacing}. Used slots: {string.Join(",", usedSlots.OrderBy(x => x).Select(x => x.ToString()).ToArray())}");
            return y;
        }


        [Server]
        public void EnsureNetworkDungeonIdentity()
        {
            IsNetworkDungeonInstance = true;

            if (string.IsNullOrEmpty(DungeonInstanceId))
            {
                DungeonInstanceId = Guid.NewGuid().ToString("N");
                Debug.Log($"[NetworkDungeonSave] Assigned dungeon instance id={DungeonInstanceId} dungeon='{name}'");
            }
        }

        public void ApplyNetworkDungeonIdentityFromData(DungeonNetworkData data)
        {
            IsNetworkDungeonInstance = data.IsNetworkDungeonInstance;
            DungeonInstanceId = data.DungeonInstanceId ?? string.Empty;
            ApplyDungeonWorldAnchorFromData(data);
            ConfigureInitialSavedActionState(data.InitialSavedActionState, "network-data-identity");
        }

        public void ConfigureRecoveredNetworkDungeonSave(float savedY, string savedInstanceId)
        {
            IsRecoveredNetworkDungeonSave = true;
            IsNetworkDungeonInstance = false;
            DungeonInstanceId = savedInstanceId ?? string.Empty;
            PositionY = savedY;
            transform.position = new Vector3(0, PositionY, 0);
            Debug.Log($"[NetworkDungeonSave] Configured recovered SP dungeon id='{DungeonInstanceId}' savedY={PositionY}");
        }

        [Server]
        public void SetDungeonWorldAnchor(int worldX, int worldZ, string reason)
        {
            HasDungeonWorldAnchor = true;
            DungeonAnchorWorldX = worldX;
            DungeonAnchorWorldZ = worldZ;
            Debug.Log($"[DungeonWorldAnchor] Set dungeon='{name}' reason={reason} anchorDF={worldX}/{worldZ} positionY={PositionY}");
        }

        public void ApplyDungeonWorldAnchorFromData(DungeonNetworkData data)
        {
            HasDungeonWorldAnchor = data.HasDungeonWorldAnchor;
            DungeonAnchorWorldX = data.DungeonAnchorWorldX;
            DungeonAnchorWorldZ = data.DungeonAnchorWorldZ;
        }

        public void WriteWorldAnchorToNetworkData(ref DungeonNetworkData data)
        {
            data.HasDungeonWorldAnchor = HasDungeonWorldAnchor;
            data.DungeonAnchorWorldX = DungeonAnchorWorldX;
            data.DungeonAnchorWorldZ = DungeonAnchorWorldZ;
        }

        public bool TryGetDungeonWorldAnchor(out int worldX, out int worldZ)
        {
            worldX = DungeonAnchorWorldX;
            worldZ = DungeonAnchorWorldZ;
            return HasDungeonWorldAnchor;
        }

        public static bool TryGetLoadedSceneDungeonEntryWorldAnchor(DFLocation location, out int worldX, out int worldZ)
        {
            worldX = 0;
            worldZ = 0;

            if (!location.Loaded)
                return false;

            DaggerfallStaticDoors[] staticDoors = FindObjectsOfType<DaggerfallStaticDoors>();
            for (int i = 0; i < staticDoors.Length; i++)
            {
                DaggerfallStaticDoors doorSet = staticDoors[i];
                if (doorSet == null || doorSet.Doors == null)
                    continue;

                bool nameMatches = doorSet.gameObject.name.Contains(location.Name) ||
                                   doorSet.gameObject.name.Contains(GetSceneName(location));
                if (!nameMatches)
                    continue;

                for (int j = 0; j < doorSet.Doors.Length; j++)
                {
                    StaticDoor door = doorSet.Doors[j];
                    if (door.doorType != DoorTypes.DungeonEntrance)
                        continue;

                    door.ownerPosition = doorSet.transform.position;
                    door.ownerRotation = doorSet.transform.rotation;

                    Vector3 doorWorld = DaggerfallStaticDoors.GetDoorPosition(door);
                    DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel(
                        (int)location.MapTableData.Longitude,
                        location.MapTableData.Latitude);
                    DFPosition baseWorld = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);

                    worldX = baseWorld.X + Mathf.RoundToInt(doorWorld.x * 40f);
                    worldZ = baseWorld.Y + Mathf.RoundToInt(doorWorld.z * 40f);

                    Debug.Log($"[DungeonWorldAnchor] Loaded scene entry anchor dungeon='{location.RegionName}/{location.Name}' baseDF={baseWorld.X}/{baseWorld.Y} doorUnity={doorWorld} anchorDF={worldX}/{worldZ}");
                    return true;
                }
            }

            return false;
        }


        [Server]
        public void SetDungeonRequesterContext(uint requesterNetId)
        {
            // IMPORTANT: In this multiplayer branch, requesterNetId == 0 is a valid host requester.
            RequesterNetId = requesterNetId;
            HasDungeonWorldAnchor = false;
            DungeonAnchorWorldX = 0;
            DungeonAnchorWorldZ = 0;

            // Prefer the actual loaded exterior dungeon entrance door over the requester's
            // PositionMultiplayer. TeleportPc can poison requester x/z with a coarse map-pixel
            // coordinate, while clicked dungeon doors already know the exact scene door transform.
            try
            {
                if (summary.LocationData.Loaded)
                {
                    int doorAnchorX;
                    int doorAnchorZ;
                    if (TryGetLoadedSceneDungeonEntryWorldAnchor(summary.LocationData, out doorAnchorX, out doorAnchorZ))
                    {
                        SetDungeonWorldAnchor(doorAnchorX, doorAnchorZ, "loaded-scene-entry-door requester=" + requesterNetId);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DungeonRequesterContext] Could not use loaded scene entry door anchor for dungeon '{name}'. Falling back to requester. error={ex.Message}");
            }

            PositionMultiplayer requesterPosition = ResolveDungeonRequesterPosition(requesterNetId);

            if (requesterPosition != null)
            {
                DungeonAnchorWorldX = requesterPosition.x;
                DungeonAnchorWorldZ = requesterPosition.z;
                HasDungeonWorldAnchor = true;

                Debug.Log($"[DungeonRequesterContext] Dungeon '{name}' requester={RequesterNetId} fallbackRequesterAnchorDF={DungeonAnchorWorldX}/{DungeonAnchorWorldZ} requesterUnity={requesterPosition.transform.position}");
            }
            else
            {
                Debug.LogWarning($"[DungeonRequesterContext] Dungeon '{name}' could not resolve PositionMultiplayer for requester={RequesterNetId}. Dungeon enemies may fallback until context is repaired.");
            }
        }

        private PositionMultiplayer ResolveDungeonRequesterPosition(uint requesterNetId)
        {
            // Host requester is represented as 0 in this project.
            if (requesterNetId == 0U)
                return ResolveHostPositionMultiplayer();

            foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
            {
                if (pm == null)
                    continue;

                NetworkIdentity ni = pm.GetComponent<NetworkIdentity>();
                if (ni != null && ni.netId == requesterNetId)
                    return pm;

                PlayerMultiplayer player = pm.GetComponent<PlayerMultiplayer>();
                if (player != null && player.netId == requesterNetId)
                    return pm;
            }

            return null;
        }

        private PositionMultiplayer ResolveHostPositionMultiplayer()
        {
            // 1) In host mode, the static local player is the host player's PlayerMultiplayer.
            if (PlayerMultiplayer.localPlayer != null)
            {
                PositionMultiplayer pm = PlayerMultiplayer.localPlayer.GetComponent<PositionMultiplayer>();
                if (pm != null)
                    return pm;
            }

            // 2) Mirror host-mode local connection identity, when available.
            if (NetworkServer.localConnection != null && NetworkServer.localConnection.identity != null)
            {
                PositionMultiplayer pm = NetworkServer.localConnection.identity.GetComponent<PositionMultiplayer>();
                if (pm != null)
                    return pm;
            }

            // 3) Server-side local player object.
            foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
            {
                if (pm == null)
                    continue;

                PlayerMultiplayer player = pm.GetComponent<PlayerMultiplayer>();
                if (player != null && player.isLocalPlayer)
                    return pm;
            }

            // 4) Explicit netId 0, for branches where the host PlayerMultiplayer really reports netId 0.
            foreach (var pm in FindObjectsOfType<PositionMultiplayer>())
            {
                if (pm == null)
                    continue;

                NetworkIdentity ni = pm.GetComponent<NetworkIdentity>();
                if (ni != null && ni.netId == 0U)
                    return pm;

                PlayerMultiplayer player = pm.GetComponent<PlayerMultiplayer>();
                if (player != null && player.netId == 0U)
                    return pm;
            }

            // 5) Last host-only fallback: choose the PositionMultiplayer closest to the real local PlayerObject.
            // This is only used for requesterNetId == 0, never for remote client requester IDs.
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
                    return best;
            }

            return null;
        }


        /// <summary>
        /// Gets the scene name for the dungeon at the given location.
        /// </summary>
        public static string GetSceneName(DFLocation location)
        {
            return string.Format("DaggerfallDungeon [Region={0}, Name={1}]", location.RegionName, location.Name);
        }


    [SerializeField]
    public DungeonSummary summary;

    public DungeonSummary Summary
    {
        get { return summary; }
        private set { summary = value; } 
    }

        public GameObject StartMarker
        {
            get { return startMarker; }
        }

        public GameObject EnterMarker
        {
            get { return enterMarker; }
        }

        public DaggerfallStaticDoors[] StaticDoorCollections
        {
            get { return EnumerateStaticDoorCollections(); }
        }

        [Serializable]
        public struct DungeonSummary
        {
            public int ID;
            public string RegionName;
            public string LocationName;
            public DFLocation LocationData;
            public DFRegion.LocationTypes LocationType;
            public DFRegion.DungeonTypes DungeonType;
        }

[System.Serializable]
public struct DungeonNetworkData
{
    public int ID;
    public string RegionName;
    public string LocationName;
    public DFRegion.LocationTypes LocationType;
    public DFRegion.DungeonTypes DungeonType;
    public float PositionY;
    public uint RequesterNetId;
    public uint DungeonNetId; // <-- Add this

    public bool IsNetworkDungeonInstance;
    public string DungeonInstanceId;

    // Stable MP dungeon world anchor. This is the DF X/Z all dungeon enemies and
    // players should use while inside this network dungeon. It must not depend on
    // transient PlayerGPS values from TeleportPc/map-pixel border positions.
    public bool HasDungeonWorldAnchor;
    public int DungeonAnchorWorldX;
    public int DungeonAnchorWorldZ;

    // Authoritative generation spec. These are primitive fields on purpose so Mirror can serialize them safely.
    // The requester computes this from the dungeon they clicked; the host uses it for enemy generation,
    // and all clients use it for local visual dungeon generation.
    public bool HasGenerationSpec;
    public int RequesterLevel;
    public int MonsterSeed;
    public int Texture0;
    public int Texture1;
    public int Texture2;
    public int Texture3;
    public int Texture4;
    public int Texture5;

    // First-creator saved door/switch/platform state. A compact validated base64
    // payload is used so Mirror only has to serialize a normal string.
    public string InitialSavedActionState;
}



public static int StableStringHash(string text)
{
    unchecked
    {
        int hash = 23;
        if (!string.IsNullOrEmpty(text))
        {
            for (int i = 0; i < text.Length; i++)
                hash = hash * 31 + text[i];
        }
        return hash;
    }
}

public static int[] BuildLocationDungeonTextureTable(DFLocation location)
{
    if (!location.Loaded || !location.HasDungeon)
        return new int[] { 119, 120, 122, 123, 124, 168 };

    int mapId = location.MapTableData.MapId;
    int locationId = location.Dungeon.RecordElement.Header.LocationId;
    bool mainStoryDungeon = IsMainStoryDungeon(mapId);
    int randomDungeonTextures = DaggerfallUnity.Settings.RandomDungeonTextures;

    // RandomTextureTableClassic() is not actually determined by locationId alone.
    // For classic/climate texture modes it reads PlayerGPS.CurrentClimateIndex.
    // Normal dungeon entry works because PlayerGPS is already at that entrance, but
    // saved-dungeon conversion can generate Castle Fällem while PlayerGPS still holds
    // Shedungent (and the host can be anywhere). Temporarily expose the destination
    // dungeon's climate only while DFU calculates the table, then restore the real GPS
    // climate immediately. This changes no coordinates, location identity, or events.
    PlayerGPS gps = null;
    int previousClimateIndex = 0;
    int destinationClimateIndex = 0;
    bool climateOverrideApplied = false;

    try
    {
        if (randomDungeonTextures < 3 ||
            (mainStoryDungeon && randomDungeonTextures != 2 && randomDungeonTextures != 4))
        {
            if (GameManager.Instance != null && GameManager.Instance.PlayerGPS != null &&
                DaggerfallUnity.Instance != null &&
                DaggerfallUnity.Instance.ContentReader.MapFileReader != null &&
                playerGPSCurrentClimateIndexField != null)
            {
                DFPosition destinationMapPixel = MapsFile.LongitudeLatitudeToMapPixel(
                    (int)location.MapTableData.Longitude,
                    location.MapTableData.Latitude);

                destinationClimateIndex = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetClimateIndex(
                    destinationMapPixel.X,
                    destinationMapPixel.Y);

                gps = GameManager.Instance.PlayerGPS;
                previousClimateIndex = (int)playerGPSCurrentClimateIndexField.GetValue(gps);
                playerGPSCurrentClimateIndexField.SetValue(gps, destinationClimateIndex);
                climateOverrideApplied = true;

                if (previousClimateIndex != destinationClimateIndex)
                {
                    Debug.Log($"[DungeonTextureLocationContext] Using destination climate for dungeon texture generation. dungeon='{location.RegionName}/{location.Name}' mapId={mapId} locationId={locationId} previousClimate={previousClimateIndex} destinationClimate={destinationClimateIndex} textureMode={randomDungeonTextures}");
                }
            }
            else
            {
                Debug.LogWarning($"[DungeonTextureLocationContext] Could not apply destination climate for dungeon='{location.RegionName}/{location.Name}'. Falling back to current PlayerGPS climate.");
            }
        }

        if (mainStoryDungeon && randomDungeonTextures != 2 && randomDungeonTextures != 4)
            return DungeonTextureTables.RandomTextureTableClassic(locationId);

        if (randomDungeonTextures < 3)
            return DungeonTextureTables.RandomTextureTableClassic(locationId, randomDungeonTextures);

        return DungeonTextureTables.RandomTextureTableAlternate(mapId);
    }
    finally
    {
        if (climateOverrideApplied && gps != null)
        {
            try
            {
                playerGPSCurrentClimateIndexField.SetValue(gps, previousClimateIndex);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DungeonTextureLocationContext] Failed to restore PlayerGPS climate after dungeon texture generation: {ex}");
            }
        }
    }
}

public static int BuildStableDungeonMonsterSeed(DFLocation location)
{
    unchecked
    {
        int seed = 17;
        seed = seed * 31 + location.MapTableData.MapId;
        seed = seed * 31 + location.Dungeon.RecordElement.Header.LocationId;
        seed = seed * 31 + (int)location.MapTableData.DungeonType;
        seed = seed * 31 + StableStringHash(location.RegionName);
        seed = seed * 31 + StableStringHash(location.Name);

        if (location.HasDungeon && location.Dungeon.Blocks != null && location.Dungeon.Blocks.Length > 0)
            seed = seed * 31 + StableStringHash(location.Dungeon.Blocks[0].BlockName);

        return seed == 0 ? location.MapTableData.MapId : seed;
    }
}

private int GetStableDungeonBlockSeed(DFLocation.DungeonBlock block, int blockIndex)
{
    unchecked
    {
        int seed = AuthoritativeMonsterSeed != 0 ? AuthoritativeMonsterSeed : BuildStableDungeonMonsterSeed(summary.LocationData);
        seed = seed * 31 + StableStringHash(block.BlockName);
        seed = seed * 31 + blockIndex;
        return seed == 0 ? Summary.ID : seed;
    }
}

private string DungeonTextureTableString()
{
    if (DungeonTextureTable == null)
        return "<null>";

    return string.Join(",", DungeonTextureTable.Select(x => x.ToString()).ToArray());
}

public void ApplyAuthoritativeGenerationSpec(int requesterLevel, int monsterSeed, int texture0, int texture1, int texture2, int texture3, int texture4, int texture5)
{
    // Do not abort if ReadyCheck fails here. This method is allowed to run before client-side deferred generation.
    // GenerateDungeon()/LayoutDungeon() still perform the normal ready check before using dfUnity.
    dfUnity = DaggerfallUnity.Instance;

    DungeonTextureTable = new int[] { texture0, texture1, texture2, texture3, texture4, texture5 };
    AuthoritativeRequesterLevel = Mathf.Clamp(requesterLevel, 1, 100);
    AuthoritativeMonsterSeed = monsterSeed != 0 ? monsterSeed : Summary.ID;
    HasAuthoritativeGenerationSpec = true;

    Debug.Log($"[DungeonGenerationSpec] Applied authoritative spec on {(NetworkServer.active ? "Server" : "Client")}: level={AuthoritativeRequesterLevel}, seed={AuthoritativeMonsterSeed}, textures=[{DungeonTextureTableString()}]");
}

public void ApplyAuthoritativeGenerationSpec(DungeonNetworkData data)
{
    if (data.HasGenerationSpec)
    {
        ApplyAuthoritativeGenerationSpec(
            data.RequesterLevel,
            data.MonsterSeed,
            data.Texture0,
            data.Texture1,
            data.Texture2,
            data.Texture3,
            data.Texture4,
            data.Texture5);
    }

    ConfigureInitialSavedActionState(data.InitialSavedActionState, "network-data-generation-spec");
}

public void WriteGenerationSpecToNetworkData(ref DungeonNetworkData data)
{
    if (DungeonTextureTable == null || DungeonTextureTable.Length < 6)
    {
        if (summary.LocationData.Loaded && summary.LocationData.HasDungeon)
            DungeonTextureTable = BuildLocationDungeonTextureTable(summary.LocationData);
        else
            DungeonTextureTable = new int[] { 119, 120, 122, 123, 124, 168 };
    }

    if (AuthoritativeMonsterSeed == 0 && summary.LocationData.Loaded && summary.LocationData.HasDungeon)
        AuthoritativeMonsterSeed = BuildStableDungeonMonsterSeed(summary.LocationData);

    if (AuthoritativeRequesterLevel <= 0)
        AuthoritativeRequesterLevel = GetLocalPlayerLevelFallback();

    data.HasGenerationSpec = true;
    data.RequesterLevel = AuthoritativeRequesterLevel;
    data.MonsterSeed = AuthoritativeMonsterSeed;
    data.Texture0 = DungeonTextureTable[0];
    data.Texture1 = DungeonTextureTable[1];
    data.Texture2 = DungeonTextureTable[2];
    data.Texture3 = DungeonTextureTable[3];
    data.Texture4 = DungeonTextureTable[4];
    data.Texture5 = DungeonTextureTable[5];
    data.InitialSavedActionState = initialSavedActionState ?? string.Empty;
}

public static void FillGenerationSpecFromLocation(ref DungeonNetworkData data, DFLocation location, int requesterLevel)
{
    int[] table = BuildLocationDungeonTextureTable(location);
    data.HasGenerationSpec = true;
    data.RequesterLevel = Mathf.Clamp(requesterLevel, 1, 100);
    data.MonsterSeed = BuildStableDungeonMonsterSeed(location);
    data.Texture0 = table[0];
    data.Texture1 = table[1];
    data.Texture2 = table[2];
    data.Texture3 = table[3];
    data.Texture4 = table[4];
    data.Texture5 = table[5];
}

public static int GetLocalPlayerLevelFallback()
{
    if (GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
        return Mathf.Clamp(GameManager.Instance.PlayerEntity.Level, 1, 100);

    return 1;
}

/// <summary>
/// Builds the compact action-state payload carried by a saved-dungeon conversion.
/// The source root is retained because ActionObjectData positions are world-space;
/// they must be translated from the old SP/MP dungeon Y slot into the new live slot.
/// </summary>
public static string SerializeInitialSavedActionState(
    ActionDoorData_v1[] actionDoors,
    ActionObjectData_v1[] actionObjects,
    Vector3 sourceDungeonRoot)
{
    int doorCount = actionDoors != null ? actionDoors.Length : 0;
    int actionCount = actionObjects != null ? actionObjects.Length : 0;
    if (doorCount == 0 && actionCount == 0)
        return string.Empty;

    if (doorCount > MaxInitialSavedActionStateEntries ||
        actionCount > MaxInitialSavedActionStateEntries)
    {
        Debug.LogWarning($"[NetworkDungeonActionState] Refused oversized local snapshot. doors={doorCount} actions={actionCount}");
        return string.Empty;
    }

    try
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(InitialSavedActionStateVersion);
            WriteVector3(writer, sourceDungeonRoot);

            writer.Write(doorCount);
            for (int i = 0; i < doorCount; i++)
            {
                ActionDoorData_v1 data = actionDoors[i];
                if (data == null)
                    data = new ActionDoorData_v1();

                writer.Write(data.loadID);
                writer.Write(data.currentLockValue);
                WriteQuaternion(writer, data.currentRotation);
                writer.Write((int)data.currentState);
                writer.Write(data.actionPercentage);
                writer.Write(data.lockpickFailedSkillLevel);
            }

            writer.Write(actionCount);
            for (int i = 0; i < actionCount; i++)
            {
                ActionObjectData_v1 data = actionObjects[i];
                if (data == null)
                    data = new ActionObjectData_v1();

                writer.Write(data.loadID);
                WriteVector3(writer, data.currentPosition);
                WriteQuaternion(writer, data.currentRotation);
                writer.Write((int)data.currentState);
                writer.Write(data.actionPercentage);
            }

            writer.Flush();
            string encoded = Convert.ToBase64String(stream.ToArray());
            if (encoded.Length > MaxInitialSavedActionStateEncodedLength)
            {
                Debug.LogWarning($"[NetworkDungeonActionState] Refused encoded snapshot larger than {MaxInitialSavedActionStateEncodedLength} characters.");
                return string.Empty;
            }

            return encoded;
        }
    }
    catch (Exception ex)
    {
        Debug.LogError($"[NetworkDungeonActionState] Failed to serialize saved action state. error={ex}");
        return string.Empty;
    }
}

/// <summary>
/// Validates and stores the first creator's snapshot. Calling this again with the
/// same network data is harmless; an already-applied live dungeon is never reset.
/// </summary>
public void ConfigureInitialSavedActionState(string encodedState, string reason)
{
    if (string.IsNullOrEmpty(encodedState))
        return;

    if (initialSavedActionStateApplied)
        return;

    if (string.Equals(initialSavedActionState, encodedState, StringComparison.Ordinal))
        return;

    Vector3 sourceRoot;
    ActionDoorData_v1[] doors;
    ActionObjectData_v1[] actions;
    if (!TryDeserializeInitialSavedActionState(encodedState, out sourceRoot, out doors, out actions))
    {
        Debug.LogWarning($"[NetworkDungeonActionState] Rejected invalid saved action snapshot. dungeon='{name}' reason={reason}");
        return;
    }

    // Re-encode parsed values so the host never relays arbitrary/unvalidated text.
    initialSavedActionState = SerializeInitialSavedActionState(doors, actions, sourceRoot);
    Debug.Log($"[NetworkDungeonActionState] Accepted first-creator snapshot. dungeon='{name}' doors={doors.Length} actions={actions.Length} sourceRoot={sourceRoot} reason={reason}");

    if (isSet)
        ScheduleInitialSavedActionStateApply(reason + "-already-generated");
}

public void ScheduleInitialSavedActionStateApply(string reason)
{
    if (InitialSavedActionStateReady || initialSavedActionStateApplyScheduled)
        return;

    initialSavedActionStateApplyScheduled = true;
    StartCoroutine(ApplyInitialSavedActionStateNextFrame(reason));
}

IEnumerator ApplyInitialSavedActionStateNextFrame(string reason)
{
    // DaggerfallAction and DaggerfallActionDoor initialise their starting transform
    // and reset CurrentState in Start(). Restore only after those Start calls run.
    yield return null;
    initialSavedActionStateApplyScheduled = false;
    ApplyInitialSavedActionStateIfReady(reason + "-after-component-start");
}

/// <summary>
/// Applies the snapshot only to serializable components below this dungeon root.
/// This avoids global LoadID collisions when several MP dungeons coexist.
/// </summary>
public void ApplyInitialSavedActionStateIfReady(string reason)
{
    if (initialSavedActionStateApplied || string.IsNullOrEmpty(initialSavedActionState))
        return;

    Vector3 sourceRoot;
    ActionDoorData_v1[] savedDoors;
    ActionObjectData_v1[] savedActions;
    if (!TryDeserializeInitialSavedActionState(
        initialSavedActionState,
        out sourceRoot,
        out savedDoors,
        out savedActions))
    {
        initialSavedActionState = string.Empty;
        Debug.LogWarning($"[NetworkDungeonActionState] Snapshot became invalid before apply. dungeon='{name}' reason={reason}");
        return;
    }

    Dictionary<ulong, SerializableActionDoor> localDoors =
        new Dictionary<ulong, SerializableActionDoor>();
    SerializableActionDoor[] doorComponents =
        GetComponentsInChildren<SerializableActionDoor>(true);
    for (int i = 0; i < doorComponents.Length; i++)
    {
        SerializableActionDoor component = doorComponents[i];
        if (component != null && component.LoadID != 0 && !localDoors.ContainsKey(component.LoadID))
            localDoors.Add(component.LoadID, component);
    }

    Dictionary<ulong, SerializableActionObject> localActions =
        new Dictionary<ulong, SerializableActionObject>();
    SerializableActionObject[] actionComponents =
        GetComponentsInChildren<SerializableActionObject>(true);
    for (int i = 0; i < actionComponents.Length; i++)
    {
        SerializableActionObject component = actionComponents[i];
        if (component != null && component.LoadID != 0 && !localActions.ContainsKey(component.LoadID))
            localActions.Add(component.LoadID, component);
    }

    int restoredDoors = 0;
    int failedDoors = 0;
    for (int i = 0; i < savedDoors.Length; i++)
    {
        ActionDoorData_v1 data = savedDoors[i];
        SerializableActionDoor component;
        if (data != null && localDoors.TryGetValue(data.loadID, out component))
        {
            try
            {
                component.RestoreSaveData(data);
                restoredDoors++;
            }
            catch (Exception ex)
            {
                failedDoors++;
                Debug.LogError($"[NetworkDungeonActionState] Failed restoring door loadID={data.loadID} dungeon='{name}' error={ex}");
            }
        }
    }

    int restoredActions = 0;
    int failedActions = 0;
    Vector3 targetRoot = transform.position;
    for (int i = 0; i < savedActions.Length; i++)
    {
        ActionObjectData_v1 data = savedActions[i];
        SerializableActionObject component;
        if (data == null || !localActions.TryGetValue(data.loadID, out component))
            continue;

        // Never mutate the retained snapshot. Restore a translated copy so late
        // clients can independently apply the original source-space values.
        ActionObjectData_v1 translated = new ActionObjectData_v1();
        translated.loadID = data.loadID;
        translated.currentPosition = targetRoot + (data.currentPosition - sourceRoot);
        translated.currentRotation = data.currentRotation;
        translated.currentState = data.currentState;
        translated.actionPercentage = data.actionPercentage;
        try
        {
            component.RestoreSaveData(translated);
            restoredActions++;
        }
        catch (Exception ex)
        {
            failedActions++;
            Debug.LogError($"[NetworkDungeonActionState] Failed restoring action loadID={data.loadID} dungeon='{name}' error={ex}");
        }
    }

    initialSavedActionStateApplied = true;
    Debug.Log($"[NetworkDungeonActionState] Applied first-creator snapshot to scoped dungeon. dungeon='{name}' restoredDoors={restoredDoors}/{savedDoors.Length} failedDoors={failedDoors} restoredActions={restoredActions}/{savedActions.Length} failedActions={failedActions} sourceRoot={sourceRoot} targetRoot={targetRoot} reason={reason}");
}

static bool TryDeserializeInitialSavedActionState(
    string encodedState,
    out Vector3 sourceDungeonRoot,
    out ActionDoorData_v1[] actionDoors,
    out ActionObjectData_v1[] actionObjects)
{
    sourceDungeonRoot = Vector3.zero;
    actionDoors = new ActionDoorData_v1[0];
    actionObjects = new ActionObjectData_v1[0];

    if (string.IsNullOrEmpty(encodedState) ||
        encodedState.Length > MaxInitialSavedActionStateEncodedLength)
        return false;

    try
    {
        byte[] bytes = Convert.FromBase64String(encodedState);
        using (MemoryStream stream = new MemoryStream(bytes, false))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            if (reader.ReadInt32() != InitialSavedActionStateVersion)
                return false;

            sourceDungeonRoot = ReadVector3(reader);
            if (!IsFiniteVector3(sourceDungeonRoot))
                return false;

            int doorCount = reader.ReadInt32();
            if (doorCount < 0 || doorCount > MaxInitialSavedActionStateEntries)
                return false;

            actionDoors = new ActionDoorData_v1[doorCount];
            for (int i = 0; i < doorCount; i++)
            {
                ActionDoorData_v1 data = new ActionDoorData_v1();
                data.loadID = reader.ReadUInt64();
                data.currentLockValue = reader.ReadInt32();
                data.currentRotation = ReadQuaternion(reader);
                data.currentState = (ActionState)reader.ReadInt32();
                data.actionPercentage = reader.ReadSingle();
                data.lockpickFailedSkillLevel = reader.ReadInt16();
                if (!IsFiniteQuaternion(data.currentRotation) || !IsFiniteFloat(data.actionPercentage))
                    return false;
                actionDoors[i] = data;
            }

            int actionCount = reader.ReadInt32();
            if (actionCount < 0 || actionCount > MaxInitialSavedActionStateEntries)
                return false;

            actionObjects = new ActionObjectData_v1[actionCount];
            for (int i = 0; i < actionCount; i++)
            {
                ActionObjectData_v1 data = new ActionObjectData_v1();
                data.loadID = reader.ReadUInt64();
                data.currentPosition = ReadVector3(reader);
                data.currentRotation = ReadQuaternion(reader);
                data.currentState = (ActionState)reader.ReadInt32();
                data.actionPercentage = reader.ReadSingle();
                if (!IsFiniteVector3(data.currentPosition) ||
                    !IsFiniteQuaternion(data.currentRotation) ||
                    !IsFiniteFloat(data.actionPercentage))
                    return false;
                actionObjects[i] = data;
            }

            return stream.Position == stream.Length;
        }
    }
    catch
    {
        sourceDungeonRoot = Vector3.zero;
        actionDoors = new ActionDoorData_v1[0];
        actionObjects = new ActionObjectData_v1[0];
        return false;
    }
}

static void WriteVector3(BinaryWriter writer, Vector3 value)
{
    writer.Write(value.x);
    writer.Write(value.y);
    writer.Write(value.z);
}

static Vector3 ReadVector3(BinaryReader reader)
{
    return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

static void WriteQuaternion(BinaryWriter writer, Quaternion value)
{
    writer.Write(value.x);
    writer.Write(value.y);
    writer.Write(value.z);
    writer.Write(value.w);
}

static Quaternion ReadQuaternion(BinaryReader reader)
{
    return new Quaternion(
        reader.ReadSingle(),
        reader.ReadSingle(),
        reader.ReadSingle(),
        reader.ReadSingle());
}

static bool IsFiniteFloat(float value)
{
    return !float.IsNaN(value) && !float.IsInfinity(value);
}

static bool IsFiniteVector3(Vector3 value)
{
    return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z);
}

static bool IsFiniteQuaternion(Quaternion value)
{
    return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) &&
           IsFiniteFloat(value.z) && IsFiniteFloat(value.w);
}





public void ScheduleDeferredGeneration(float authoritativePositionY = float.NaN)
{
    dfUnity = DaggerfallUnity.Instance; // ✅ Ensure dfUnity is initialized

    // If the caller has host-authored Y in RPC data, apply it immediately before the deferred generation coroutine.
    // This keeps transform.position and PositionY aligned even if the SyncVar has not arrived yet on this frame.
    if (!float.IsNaN(authoritativePositionY))
    {
        PositionY = authoritativePositionY;
        transform.position = new Vector3(0, PositionY, 0);
    }

    StartCoroutine(DeferredClientGeneration());
}

private IEnumerator DeferredClientGeneration()
{
    yield return new WaitForSeconds(0.5f);

    // Apply the current authoritative PositionY value. This is either already set from RPC data or from the SyncVar.
    transform.position = new Vector3(0, PositionY, 0);
    Debug.Log($"[DeferredClientGeneration] Applied PositionY: {PositionY}");

    if (!isSet && summary.LocationData.Loaded)
    {
        Debug.Log($"[DeferredClientGeneration] Generating blocks for {summary.LocationName}");

        // Texture setup. If the host sent an authoritative spec, use that exact table.
        // Otherwise fall back to local DFU texture generation.
        if (HasAuthoritativeGenerationSpec)
        {
            Debug.Log($"[DeferredClientGeneration] Using authoritative generation spec: level={AuthoritativeRequesterLevel}, seed={AuthoritativeMonsterSeed}, textures=[{DungeonTextureTableString()}]");
        }
        else if (DungeonTextureUse == DaggerfallWorkshop.DungeonTextureUse.UseLocation_PartiallyImplemented)
        {
            UseLocationDungeonTextureTable();
            AuthoritativeMonsterSeed = BuildStableDungeonMonsterSeed(summary.LocationData);
            AuthoritativeRequesterLevel = GetLocalPlayerLevelFallback();
            Debug.Log($"[DeferredClientGeneration] No authoritative spec present. Locally generated fallback textures=[{DungeonTextureTableString()}], level={AuthoritativeRequesterLevel}, seed={AuthoritativeMonsterSeed}");
        }

        LayoutDungeon(summary.LocationData, false);
        isSet = true;
        ScheduleInitialSavedActionStateApply("deferred-client-generation");
    }
    else
    {
        Debug.LogWarning("[DeferredClientGeneration] Skipped generation: either already set or location not loaded.");
    }
}







public void SetDungeon(DFLocation location, bool importEnemies = true)
{
    if (!ReadyCheck())
        return;

    bool multiplayerActive = NetworkServer.active || NetworkClient.active;

    // Singleplayer/local dungeon path. Do not instantiate network prefabs, do not NetworkServer.Spawn(),
    // and do not move the dungeon to a negative network Y slot.
    if (!multiplayerActive)
    {
        GenerateDungeon(location, importEnemies, IsRecoveredNetworkDungeonSave ? PositionY : 0f);
        return;
    }

    // 🔹 If client, request from host
    if (NetworkClient.active && !NetworkServer.active)
    {
        PlayerMultiplayer player = null;

        if (NetworkClient.connection != null && NetworkClient.connection.identity != null)
            player = NetworkClient.connection.identity.GetComponent<PlayerMultiplayer>();

        if (player == null)
        {
            foreach (var candidate in FindObjectsOfType<PlayerMultiplayer>())
            {
                if (candidate != null && candidate.isLocalPlayer)
                {
                    player = candidate;
                    break;
                }
            }
        }

        if (player != null)
        {
            Debug.Log($"[SetDungeon] Client requesting dungeon from host: {location.RegionName} - {location.Name}");

            uint myNetId = 0U;
            if (NetworkClient.connection != null && NetworkClient.connection.identity != null)
                myNetId = NetworkClient.connection.identity.netId;
            if (myNetId == 0U)
                myNetId = player.netId;

            Debug.Log($"[SetDungeon] Dungeon requester local player='{player.name}' playerNetId={player.netId} connectionIdentityNetId={myNetId}");

            DungeonNetworkData requestSpec = new DungeonNetworkData
            {
                ID = location.MapTableData.MapId,
                RegionName = location.RegionName,
                LocationName = location.Name,
                LocationType = location.MapTableData.LocationType,
                DungeonType = location.MapTableData.DungeonType,
                RequesterNetId = myNetId
            };

            int requesterLevel = GetLocalPlayerLevelFallback();
            FillGenerationSpecFromLocation(ref requestSpec, location, requesterLevel);

            Debug.Log($"[SetDungeon] Sending requester generation spec to host: level={requestSpec.RequesterLevel}, seed={requestSpec.MonsterSeed}, textures=[{requestSpec.Texture0},{requestSpec.Texture1},{requestSpec.Texture2},{requestSpec.Texture3},{requestSpec.Texture4},{requestSpec.Texture5}]");

            player.CmdRequestDungeonFromHostWithGenerationSpec(
                location.RegionName,
                location.Name,
                myNetId,
                requestSpec.RequesterLevel,
                requestSpec.MonsterSeed,
                requestSpec.Texture0,
                requestSpec.Texture1,
                requestSpec.Texture2,
                requestSpec.Texture3,
                requestSpec.Texture4,
                requestSpec.Texture5);
            return;
        }

        Debug.LogError("[SetDungeon] ERROR: No PlayerMultiplayer found!");
        return;
    }

    // 🔹 Host check for existing dungeon
    string dungeonSceneName = GetSceneName(location);
    DaggerfallDungeon[] existingDungeons = FindObjectsOfType<DaggerfallDungeon>();
    foreach (var dungeon in existingDungeons)
    {
        if (dungeon.Summary.LocationName == location.Name &&
            dungeon.Summary.RegionName == location.RegionName)
        {
            Debug.Log($"[SetDungeon] Dungeon already exists: {dungeonSceneName}");
            if (!dungeon.isSet)
                dungeon.GenerateDungeon(location, importEnemies);

            TransitionHostToDungeon(dungeon, location);
            return;
        }
    }

    // 🔹 Spawn networked dungeon prefab
    GameObject prefab = NetworkManager.singleton.spawnPrefabs
        .FirstOrDefault(p => p.GetComponent<DaggerfallDungeon>() != null);

    if (prefab == null)
    {
        Debug.LogError("[SetDungeon] ERROR: DaggerfallDungeon prefab not found in spawnable prefabs!");
        return;
    }

    GameObject dungeonObj = Instantiate(prefab);
    dungeonObj.name = dungeonSceneName;

    DaggerfallDungeon dungeonComp = dungeonObj.GetComponent<DaggerfallDungeon>();
    dungeonComp.PositionY = dungeonObj.transform.position.y;
    dungeonComp.SetDungeonRequesterContext(0);
    dungeonComp.EnsureNetworkDungeonIdentity();

    NetworkServer.Spawn(dungeonObj);
    dungeonComp.GenerateDungeon(location, importEnemies); // host generates it

    // 🔹 Sync dungeon data to all clients
DaggerfallDungeon.DungeonNetworkData data = new DaggerfallDungeon.DungeonNetworkData
{
    ID = dungeonComp.Summary.ID,
    RegionName = dungeonComp.Summary.RegionName,
    LocationName = dungeonComp.Summary.LocationName,
    LocationType = dungeonComp.Summary.LocationType,
    DungeonType = dungeonComp.Summary.DungeonType,
    PositionY = dungeonComp.PositionY,
    RequesterNetId = dungeonComp.RequesterNetId,
    DungeonNetId = dungeonComp.netId, // ✅ ADD THIS
    IsNetworkDungeonInstance = dungeonComp.IsNetworkDungeonInstance,
    DungeonInstanceId = dungeonComp.DungeonInstanceId,
};
dungeonComp.WriteGenerationSpecToNetworkData(ref data);
dungeonComp.WriteWorldAnchorToNetworkData(ref data);
Debug.Log($"[SetDungeon] Sync spec prepared: level={data.RequesterLevel}, seed={data.MonsterSeed}, textures=[{data.Texture0},{data.Texture1},{data.Texture2},{data.Texture3},{data.Texture4},{data.Texture5}]");

    foreach (PlayerMultiplayer player in FindObjectsOfType<PlayerMultiplayer>())
    {
        if (player.connectionToClient != null)
        {
            player.RpcSyncDungeon(data);
        }
    }

    TransitionHostToDungeon(dungeonComp, location);
}


private void TransitionHostToDungeon(DaggerfallDungeon dungeonManager, DFLocation location)
{
    if (dungeonManager == null)
    {
        Debug.LogError("[TransitionHostToDungeon] ERROR: Dungeon manager is null!");
        return;
    }

    GameObject dungeonParent = GameObject.Find("Dungeon");
    if (dungeonParent != null)
        dungeonParent.SetActive(true);

    StaticDoor? entryDoorNullable = GetDungeonEntryDoor(location);
    if (!entryDoorNullable.HasValue)
    {
        Debug.LogError($"[TransitionHostToDungeon] ERROR: No entry door found for {location.Name}");
        return;
    }

    StaticDoor entryDoor = entryDoorNullable.Value;

    PlayerEnterExit playerEnterExit = FindObjectOfType<PlayerEnterExit>();
    if (playerEnterExit != null && !playerEnterExit.IsPlayerInsideDungeon)
    {
        Debug.Log($"[TransitionHostToDungeon] Entering dungeon: {location.Name}");
        playerEnterExit.TransitionDungeonInterior(null, entryDoor, location, false);
    }
}


private PlayerGPSLocationOverrideState PushPlayerGPSLocationForDungeonEnemyImport(DFLocation location, bool importEnemies)
{
    PlayerGPSLocationOverrideState state = new PlayerGPSLocationOverrideState();

    // RDBLayout.AddRandomEnemies() classic mode seeds random dungeon enemies from
    // GameManager.Instance.PlayerGPS.CurrentLocation instead of the DFLocation passed
    // into DaggerfallDungeon.LayoutDungeon().
    // In multiplayer, the host can be physically somewhere else when generating a
    // client-requested dungeon, so temporarily point the host GPS location at the
    // requested dungeon only while GameObjectHelper imports dungeon enemies.
    if (!importEnemies || !NetworkServer.active)
        return state;

    if (GameManager.Instance == null || GameManager.Instance.PlayerGPS == null)
        return state;

    if (playerGPSCurrentLocationField == null || playerGPSHasCurrentLocationField == null)
    {
        Debug.LogWarning("[DungeonEnemyLocationContext] Could not find PlayerGPS private location fields. Host-away dungeon enemy generation may still use host current location.");
        return state;
    }

    PlayerGPS gps = GameManager.Instance.PlayerGPS;

    try
    {
        state.GPS = gps;
        state.PreviousLocation = (DFLocation)playerGPSCurrentLocationField.GetValue(gps);
        state.PreviousHasCurrentLocation = (bool)playerGPSHasCurrentLocationField.GetValue(gps);
        state.Applied = true;

        int previousMapId = state.PreviousLocation.Loaded ? state.PreviousLocation.MapTableData.MapId : -1;
        int previousLocationId = (state.PreviousLocation.Loaded && state.PreviousLocation.HasDungeon) ? state.PreviousLocation.Dungeon.RecordElement.Header.LocationId : -1;
        int requestedMapId = location.Loaded ? location.MapTableData.MapId : -1;
        int requestedLocationId = (location.Loaded && location.HasDungeon) ? location.Dungeon.RecordElement.Header.LocationId : -1;

        playerGPSCurrentLocationField.SetValue(gps, location);
        playerGPSHasCurrentLocationField.SetValue(gps, true);

        Debug.Log($"[DungeonEnemyLocationContext] Overriding host PlayerGPS location during dungeon enemy import. previousMapId={previousMapId}, previousLocationId={previousLocationId}, requestedMapId={requestedMapId}, requestedLocationId={requestedLocationId}, location='{location.RegionName} - {location.Name}'");
    }
    catch (Exception ex)
    {
        Debug.LogError($"[DungeonEnemyLocationContext] Failed to override host PlayerGPS location for dungeon enemy import: {ex}");
        state.Applied = false;
    }

    return state;
}

private void PopPlayerGPSLocationForDungeonEnemyImport(PlayerGPSLocationOverrideState state)
{
    if (!state.Applied || state.GPS == null)
        return;

    try
    {
        playerGPSCurrentLocationField.SetValue(state.GPS, state.PreviousLocation);
        playerGPSHasCurrentLocationField.SetValue(state.GPS, state.PreviousHasCurrentLocation);

        int restoredMapId = state.PreviousLocation.Loaded ? state.PreviousLocation.MapTableData.MapId : -1;
        int restoredLocationId = (state.PreviousLocation.Loaded && state.PreviousLocation.HasDungeon) ? state.PreviousLocation.Dungeon.RecordElement.Header.LocationId : -1;
        Debug.Log($"[DungeonEnemyLocationContext] Restored host PlayerGPS location after dungeon enemy import. restoredMapId={restoredMapId}, restoredLocationId={restoredLocationId}");
    }
    catch (Exception ex)
    {
        Debug.LogError($"[DungeonEnemyLocationContext] Failed to restore host PlayerGPS location after dungeon enemy import: {ex}");
    }
}


        /// <summary>
        /// Server-side metadata stamp for enemies imported during LayoutDungeon().
        /// This runs in the same call stack as dungeon generation, before isSet/TargetEnter,
        /// so TeleportPc dungeons cannot sit for several real seconds with tavern/coarse DF X/Z
        /// while the host is in a popup/menu.
        ///
        /// Metadata only: does not move enemies, does not change authority, and does not touch
        /// DynamicEnemyAuthority. It only forces imported dungeon EnemyWorldPosition objects to
        /// use this dungeon's already-authored DF world anchor.
        /// </summary>
        private void StampImportedDungeonEnemyAnchors(string reason)
        {
            if (!NetworkServer.active)
                return;

            // Only network/server dungeons need this. Singleplayer local dungeons use vanilla state.
            if (!IsNetworkDungeonInstance && Mathf.Abs(PositionY) < 1f)
                return;

            if (!HasDungeonWorldAnchor)
            {
                Debug.LogWarning($"[DungeonEnemyAnchor] Cannot stamp imported enemies for dungeon='{name}' because no dungeon world anchor is set. requester={RequesterNetId} reason={reason}");
                return;
            }

            global::EnemyWorldPosition[] worldPositions = GetComponentsInChildren<global::EnemyWorldPosition>(true);
            int count = 0;
            int changed = 0;

            for (int i = 0; i < worldPositions.Length; i++)
            {
                global::EnemyWorldPosition ewp = worldPositions[i];
                if (ewp == null)
                    continue;

                // Never touch player/avatar objects even if a shared visual prefab has enemy-style components.
                if (ewp.GetComponentInParent<global::PlayerMultiplayer>() != null)
                    continue;

                int oldX = ewp.worldX;
                int oldZ = ewp.worldZ;
                bool oldDungeonSpawn = ewp.isDungeonSpawn;
                bool oldHasAnchor = ewp.hasDungeonWorldAnchor;

                ewp.SetDungeonSpawnContextLocked(RequesterNetId, DungeonAnchorWorldX, DungeonAnchorWorldZ, true, reason);
                count++;

                if (oldX != DungeonAnchorWorldX ||
                    oldZ != DungeonAnchorWorldZ ||
                    !oldDungeonSpawn ||
                    !oldHasAnchor)
                {
                    changed++;
                    Debug.Log($"[DungeonEnemyAnchor] Stamped enemy='{ewp.name}' old={oldX}/{oldZ} new={DungeonAnchorWorldX}/{DungeonAnchorWorldZ} requester={RequesterNetId} dungeon='{name}' reason={reason}");
                }
            }

            Debug.Log($"[DungeonEnemyAnchor] Stamped {count} imported dungeon enemy anchor(s), changed={changed}. dungeon='{name}' requester={RequesterNetId} anchor={DungeonAnchorWorldX}/{DungeonAnchorWorldZ} reason={reason}");
        }

public void GenerateDungeon(DFLocation location, bool importEnemies, float assignedY = -1f)
{
    Debug.Log($"[GenerateDungeon] Attempting to generate dungeon: {location.RegionName} - {location.Name}");

    if (!ReadyCheck())
    {
        Debug.LogError("[GenerateDungeon] ERROR: DaggerfallUnity is not ready. Aborting dungeon generation.");
        return;
    }

    if (gameObject != null)
    {
        gameObject.SetActive(true);
    }
    else
    {
        Debug.LogError("[GenerateDungeon] ERROR: Dungeon GameObject is missing!");
        return;
    }

    if (this.isSet)
    {
        Debug.LogWarning($"[GenerateDungeon] Dungeon already set: {location.RegionName} - {location.Name}. Skipping re-generation.");
        return;
    }

    if (!location.Loaded)
        throw new Exception("DFLocation not loaded.");
    if (!location.HasDungeon)
        throw new Exception("DFLocation does not contain a dungeon.");

    summary = new DungeonSummary
    {
        ID = location.MapTableData.MapId,
        RegionName = location.RegionName,
        LocationName = location.Name,
        LocationData = location,
        LocationType = location.MapTableData.LocationType,
        DungeonType = location.MapTableData.DungeonType
    };

    Debug.Log($"[GenerateDungeon] Assigned Dungeon Data: {summary.RegionName} - {summary.LocationName}");

    bool multiplayerActive = NetworkServer.active || NetworkClient.active;

    if (!multiplayerActive)
    {
        // In normal singleplayer the dungeon remains at the normal DFU dungeon origin.
        // When recovering a save made inside a network dungeon, keep the saved MP Y slot
        // so the saved player/enemy positions still match the reconstructed local dungeon.
        if (!IsRecoveredNetworkDungeonSave)
            assignedY = 0f;
        else if (Mathf.Approximately(assignedY, -1f))
            assignedY = PositionY;
    }


if (NetworkServer.active)
{
    EnsureNetworkDungeonIdentity();

    // If caller did not provide a real negative Y, allocate a host-authoritative free slot.
    // Do not recalculate from dungeon count here; this object may already exist and destroyed dungeons make count-based slots unsafe.
    if (assignedY >= 0f || Mathf.Approximately(assignedY, -1f))
        assignedY = GetNextAvailableDungeonY(this);

    Debug.Log($"[GenerateDungeon] Using host-authoritative assigned Y = {assignedY}");
}
else if (multiplayerActive && (assignedY >= 0f || Mathf.Approximately(assignedY, -1f)))
{
    // Clients should normally receive a host-authored Y through DungeonNetworkData/SyncVar.
    assignedY = PositionY;
    Debug.Log($"[GenerateDungeon] Client fallback using existing PositionY = {assignedY}");
}


    // 🔹 Apply the calculated Y position
    PositionY = assignedY;
    transform.position = new Vector3(0, PositionY, 0);
    Debug.Log($"[GenerateDungeon] Dungeon {summary.LocationName} assigned PositionY = {PositionY}");

    if (HasAuthoritativeGenerationSpec)
    {
        Debug.Log($"[GenerateDungeon] Using requester-authoritative generation spec: level={AuthoritativeRequesterLevel}, seed={AuthoritativeMonsterSeed}, textures=[{DungeonTextureTableString()}]");
    }
    else if (DungeonTextureUse == DaggerfallWorkshop.DungeonTextureUse.UseLocation_PartiallyImplemented)
    {
        UseLocationDungeonTextureTable();
        AuthoritativeMonsterSeed = BuildStableDungeonMonsterSeed(location);
        AuthoritativeRequesterLevel = GetLocalPlayerLevelFallback();
        Debug.Log($"[GenerateDungeon] Generated local/host generation spec: level={AuthoritativeRequesterLevel}, seed={AuthoritativeMonsterSeed}, textures=[{DungeonTextureTableString()}]");
    }

    startMarker = null;
    PlayerGPSLocationOverrideState gpsLocationOverride = PushPlayerGPSLocationForDungeonEnemyImport(location, importEnemies);
    try
    {
        LayoutDungeon(location, importEnemies);

        if (importEnemies)
            StampImportedDungeonEnemyAnchors("GenerateDungeon-after-layout-before-isSet");
    }
    finally
    {
        PopPlayerGPSLocationForDungeonEnemyImport(gpsLocationOverride);
    }

    isSet = true;
    ScheduleInitialSavedActionStateApply("host-or-local-generation");
    RaiseOnSetDungeonEvent();
	
if (NetworkServer.active)
{
DaggerfallDungeon.DungeonNetworkData data = new DaggerfallDungeon.DungeonNetworkData
{
    ID = summary.ID,
    RegionName = summary.RegionName,
    LocationName = summary.LocationName,
    LocationType = summary.LocationType,
    DungeonType = summary.DungeonType,
    PositionY = PositionY,
    RequesterNetId = this.RequesterNetId,
    DungeonNetId = netId, // ✅ Add this so clients can match the correct instance
    IsNetworkDungeonInstance = IsNetworkDungeonInstance,
    DungeonInstanceId = DungeonInstanceId,
};
WriteGenerationSpecToNetworkData(ref data);
WriteWorldAnchorToNetworkData(ref data);
Debug.Log($"[GenerateDungeon] Sync spec prepared: level={data.RequesterLevel}, seed={data.MonsterSeed}, textures=[{data.Texture0},{data.Texture1},{data.Texture2},{data.Texture3},{data.Texture4},{data.Texture5}]");

foreach (PlayerMultiplayer player in FindObjectsOfType<PlayerMultiplayer>())
{
    Debug.Log($"[GenerateDungeon] Found player: netId={player.netId}, isLocalPlayer={player.isLocalPlayer}");

    if (player.connectionToClient != null)
    {
        Debug.Log($"[GenerateDungeon] Sending RpcSyncDungeon to: {player.netId}");
        player.RpcSyncDungeon(data);
    }
    else
    {
        Debug.LogWarning($"[GenerateDungeon] No connectionToClient for: {player.netId}");
    }
}
}
}


	


// 🔹 Finds the correct entry door for the dungeon
public static StaticDoor? GetDungeonEntryDoor(DFLocation location)
{
    Debug.Log($"[GetDungeonEntryDoor] Searching for dungeon entry door for {location.Name}");

    DaggerfallStaticDoors[] staticDoors = FindObjectsOfType<DaggerfallStaticDoors>();

    foreach (DaggerfallStaticDoors doorSet in staticDoors)
    {
        foreach (StaticDoor door in doorSet.Doors)
        {
            // 🔹 Check if door is a dungeon entrance AND matches the dungeon name
            if (door.doorType == DoorTypes.DungeonEntrance && 
                (doorSet.gameObject.name.Contains(location.Name) || doorSet.gameObject.name.Contains(GetSceneName(location))))
            {
                StaticDoor ownedDoor = door;
                ownedDoor.ownerPosition = doorSet.transform.position;
                ownedDoor.ownerRotation = doorSet.transform.rotation;
                Debug.Log($"[GetDungeonEntryDoor] Found matching entry door for {location.Name} at {doorSet.gameObject.name}");
                return ownedDoor;
            }
        }
    }

    Debug.LogWarning($"[GetDungeonEntryDoor] No matching entry door found for {location.Name}. Falling back to first available entrance.");

    // 🔹 Fallback: If no exact match is found, return the first available dungeon entrance
    foreach (DaggerfallStaticDoors doorSet in staticDoors)
    {
        foreach (StaticDoor door in doorSet.Doors)
        {
            if (door.doorType == DoorTypes.DungeonEntrance)
            {
                StaticDoor ownedDoor = door;
                ownedDoor.ownerPosition = doorSet.transform.position;
                ownedDoor.ownerRotation = doorSet.transform.rotation;
                Debug.Log($"[GetDungeonEntryDoor] Using fallback entry door at {doorSet.gameObject.name}");
                return ownedDoor;
            }
        }
    }

    Debug.LogError($"[GetDungeonEntryDoor] No fallback dungeon entry found for {location.Name}");
    return null;
}

// 🔹 Gets the entry door for the currently assigned dungeon summary (For Clients)
public StaticDoor? GetDungeonEntryDoor()
{
    if (!summary.LocationData.Loaded)  // Check if the location is properly loaded
    {
        Debug.LogError("[GetDungeonEntryDoor] ERROR: Dungeon summary has invalid LocationData!");
        return null;
    }
    return GetDungeonEntryDoor(summary.LocationData);
}

/*
public void ApplySummary(DungeonNetworkData data)
{
    var mapReader = DaggerfallUnity.Instance.ContentReader.MapFileReader;

    // Force-load the location which auto-populates Dungeon if available
    DFLocation location = mapReader.GetLocation(data.RegionName, data.LocationName);

    if (!location.Loaded)
    {
        Debug.LogError($"[ApplySummary] ERROR: Could not load location data for {data.RegionName} - {data.LocationName}");
        return;
    }

    if (!location.HasDungeon || location.Dungeon.Blocks == null || location.Dungeon.Blocks.Length == 0)
    {
        Debug.LogError($"[ApplySummary] ERROR: Dungeon blocks missing for {data.LocationName}.");
        return;
    }

    summary = new DungeonSummary
    {
        ID = data.ID,
        RegionName = data.RegionName,
        LocationName = data.LocationName,
        LocationType = data.LocationType,
        DungeonType = data.DungeonType,
        LocationData = location
    };

    PositionX = data.PositionX;
    transform.position = new Vector3(PositionX, 0, 0);

    Debug.Log($"[ApplySummary] Applied summary for {summary.RegionName} - {summary.LocationName}. Blocks={location.Dungeon.Blocks.Length}");
}*/









        public void ResetDungeonTextureTable()
        {
            DungeonTextureTable[0] = 119;
            DungeonTextureTable[1] = 120;
            DungeonTextureTable[2] = 122;
            DungeonTextureTable[3] = 123;
            DungeonTextureTable[4] = 124;
            DungeonTextureTable[5] = 168;
            ApplyDungeonTextureTable();
        }

        public void RandomiseDungeonTextureTable()
        {
            DungeonTextureTable = DungeonTextureTables.RandomTextureTableAlternate(UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            ApplyDungeonTextureTable();
        }

        /// <summary>
        /// Helper to check if dungeon is a main story dungeon.
        /// </summary>
        /// <param name="id">ID of dungeon.</param>
        /// <returns>True if dungeon is a main story dungeon.</returns>
        public static bool IsMainStoryDungeon(int id)
        {
            bool mainStoryDungeon = false;
            switch (id)
            {
                case 187853213:         // Daggerfall/Privateer's Hold
                case 630439035:         // Wayrest/Wayrest
                case 1291010263:        // Daggerfall/Daggerfall
                case 6634853:           // Sentinel/Sentinel
                case 19021260:          // Orsinium Area/Orsinium
                case 728811286:         // Wrothgarian Mountains/Shedungent
                case 701948302:         // Dragontail Mountains/Scourg Barrow
                case 83032363:          // Wayrest/Woodborne Hall
                case 1001:              // High Rock sea coast/Mantellan Crux
                case 207828842:         // Menevia/Lysandus' Tomb
                case 9570447:           // Daggerfall/Castle Necromoghan
                case 2352284:           // Betony/Tristore Laboratory
                case 336619236:         // Ykalon/Castle Llugwych
                case 43196334:          // Isle of Balfiera/Direnni Tower
                    mainStoryDungeon = true;
                    break;
                default:
                    break;
            }

            return mainStoryDungeon;
        }

        public void UseLocationDungeonTextureTable()
        {
            // Generates dungeon texture table from random seed
            // RandomDungeonTextures are read from settings.ini. Values are
            // 0 : Classic textures (swamp and woodland texture sets unused)
            // 1 : Textures by climate + classic textures for main story dungeons
            // 2 : Textures by climate for all dungeons
            // 3 : Randomized + classic textures for main story dungeons (method used in earlier DF Unity builds)
            // 4 : Randomized for all dungeons
            bool mainStoryDungeon = IsMainStoryDungeon(Summary.ID);
            int randomDungeonTextures = DaggerfallUnity.Settings.RandomDungeonTextures;
            // If not overriding with other textures (modes 2 and 4), use classic algorithm for main story dungeons
            if (mainStoryDungeon && randomDungeonTextures != 2 && randomDungeonTextures != 4)
                DungeonTextureTable = DungeonTextureTables.RandomTextureTableClassic(Summary.LocationData.Dungeon.RecordElement.Header.LocationId);
            else // Otherwise, use a random texture according to the mode set in settings.ini
            {
                if (randomDungeonTextures < 3)
                    DungeonTextureTable = DungeonTextureTables.RandomTextureTableClassic(Summary.LocationData.Dungeon.RecordElement.Header.LocationId, DaggerfallUnity.Settings.RandomDungeonTextures);
                else
                    DungeonTextureTable = DungeonTextureTables.RandomTextureTableAlternate(Summary.ID);
            }
            ApplyDungeonTextureTable();
        }

        public void ApplyDungeonTextureTable()
        {
            // Do nothing if not ready
            if (!ReadyCheck())
                return;

            // Process all DaggerfallMesh child components
            DaggerfallMesh[] meshArray = GetComponentsInChildren<DaggerfallMesh>();
            foreach (var dm in meshArray)
            {
                dm.SetDungeonTextures(DungeonTextureTable);
            }
        }

        public int GetPlayerBlockIndex(Vector3 playerPos)
        {
            if (!summary.LocationData.Loaded)
                return -1;

            // Check if player is inside any block of dungeon
            // RDB blocks are laid out in 2D and have no vertical extents
            // We can just check using rects, which is very fast
            Rect rect = new Rect();
            DFLocation.DungeonBlock block;
            Vector2 pos = new Vector2(playerPos.x, playerPos.z);
            for (int i = 0; i < summary.LocationData.Dungeon.Blocks.Length; i++)
            {
                block = summary.LocationData.Dungeon.Blocks[i];
                rect.xMin = transform.position.x + block.X * RDBLayout.RDBSide;
                rect.xMax = rect.xMin + RDBLayout.RDBSide;
                rect.yMin = transform.position.z + block.Z * RDBLayout.RDBSide;
                rect.yMax = rect.yMin + RDBLayout.RDBSide;

                if (rect.Contains(pos))
                    return i;
            }

            return -1;
        }

        public bool GetBlockData(int index, out DFLocation.DungeonBlock blockDataOut)
        {
            if (!summary.LocationData.Loaded)
            {
                blockDataOut = new DFLocation.DungeonBlock();
                return false;
            }

            blockDataOut = summary.LocationData.Dungeon.Blocks[index];

            return true;
        }

        /// <summary>
        /// Gets special dungeon name (e.g. Castle Daggerfall).
        /// Fallback to current location name if not in a special named dungeon.
        /// </summary>
        public string GetSpecialDungeonName()
        {
            string dungeonName = string.Empty;
            if (summary.RegionName == "Daggerfall" && summary.LocationName == "Daggerfall")
                dungeonName = DaggerfallUnity.Instance.TextProvider.GetText(475);
            else if (summary.RegionName == "Wayrest" && summary.LocationName == "Wayrest")
                dungeonName = DaggerfallUnity.Instance.TextProvider.GetText(476);
            else if (summary.RegionName == "Sentinel" && summary.LocationName == "Sentinel")
                dungeonName = DaggerfallUnity.Instance.TextProvider.GetText(477);
            else
                dungeonName = summary.LocationName;

            return dungeonName.TrimEnd('.');
        }

        /// <summary>
        /// Gets all debugger marker positions for this dungeon.
        /// Not related to gameplay systems like quests. Only used for teleporting player around marker positions using quest debugger.
        /// </summary>
        /// <returns>Array of all debugger marker positions. Can return null or empty.</returns>
        public Vector3[] GetAllDebuggerMarkerPositions()
        {
            EnumerateDebuggerMarkers();
            if (debuggerMarkerPositions != null && debuggerMarkerPositions.Count > 0)
                return debuggerMarkerPositions.ToArray();
            else
                return null;
        }

        #region Private Methods

public void LayoutDungeon(in DFLocation location, bool importEnemies = true)
{
#if SHOW_LAYOUT_TIMES
    // Start timing
    System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
    long startTime = stopwatch.ElapsedMilliseconds;
#endif
    using (s_LayoutDungeonMarker.Auto())
    {
        // Get player level. Networked dungeons use the requester-authoritative level supplied by the player who clicked the entrance.
        float playerLevel = 1;
        if (AuthoritativeRequesterLevel > 0)
            playerLevel = AuthoritativeRequesterLevel;
        else if (Application.isPlaying && GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
            playerLevel = GameManager.Instance.PlayerEntity.Level;

        // Calculate monster power - this is a clamped 0-1 value based on player's level from 1-20
        float monsterPower = Mathf.Clamp01(playerLevel / 20f);

        // Create dungeon layout
        for (int i = 0; i < summary.LocationData.Dungeon.Blocks.Length; i++)
        {
            DFLocation.DungeonBlock block = summary.LocationData.Dungeon.Blocks[i];
            int blockSeed = GetStableDungeonBlockSeed(block, i);
            if (i == 0)
            {
                Debug.Log($"[DungeonGenerationSpec:LayoutFirstBlock] side={(NetworkServer.active ? "Server" : "Client")} block='{block.BlockName}' dungeonType={Summary.DungeonType} playerLevel={playerLevel} monsterPower={monsterPower} variance={RandomMonsterVariance} seed={blockSeed} importEnemies={importEnemies} textures=[{DungeonTextureTableString()}]");
            }

            GameObject go = GameObjectHelper.CreateRDBBlockGameObject(
                block.BlockName,
                DungeonTextureTable,
                block.IsStartingBlock,
                Summary.DungeonType,
                monsterPower,
                RandomMonsterVariance,
                blockSeed,
                dfUnity.Option_DungeonBlockPrefab,
                importEnemies,
                Mathf.Clamp(Mathf.RoundToInt(playerLevel), 1, 100));
            go.transform.parent = this.transform;

            // 🔹 Fix: Offset each block by the dungeon's PositionY to prevent overlapping
go.transform.position = new Vector3(
    block.X * RDBLayout.RDBSide,      // X remains X
    PositionY,                        // ✅ Use Y here to move dungeon vertically
    block.Z * RDBLayout.RDBSide       // Z remains Z
);

            DaggerfallRDBBlock daggerfallBlock = go.GetComponent<DaggerfallRDBBlock>();
            if (block.IsStartingBlock)
                FindMarkers(daggerfallBlock, ref block, true); // Assign start marker and enter marker
            else
                FindMarkers(daggerfallBlock, ref block, false); // Only find water level and palaceblock info from start marker

            summary.LocationData.Dungeon.Blocks[i].WaterLevel = block.WaterLevel;
            summary.LocationData.Dungeon.Blocks[i].CastleBlock = block.CastleBlock;

            // Add water blocks
            RDBLayout.AddWater(go, go.transform.position, block.WaterLevel);
        }

        RemoveOverlappingDoors();
    }

#if SHOW_LAYOUT_TIMES
    // Show timer
    long totalTime = stopwatch.ElapsedMilliseconds - startTime;
    DaggerfallUnity.LogMessage(string.Format("Time to layout dungeon: {0}ms", totalTime), true);
#endif
}


        // Remove duplicate/overlapping action doors in dungeons
        // Action doors that are intentionally placed flush next to each other (e.g. entrance in Daggerfall Castle) have ~2.25 units between origins
        // Using a tolerance of <1.4 and culling a door found within this limit - any doors this close together are definitely overlapping
        // Note some doors appear sunken in doorframe - not trying to select "best" door based on placement height at this time
        // This process adds approx. 7ms layout time per 100 doors processed - hardly noticeable above layout times of ~1250ms for large dungeons (e.g. Scourg, 206 doors)
        // TODO:
        //  * Try to identify the "best" aligned door
        private void RemoveOverlappingDoors()
        {
            const float tolerance = 1.4f;

            List<Vector3> doorPosRegistry = new List<Vector3>();
            DaggerfallStaticDoors[] staticDoorCollections = EnumerateStaticDoorCollections();
            DaggerfallActionDoor[] actionDoors = GetComponentsInChildren<DaggerfallActionDoor>();

            // Add static exit door to registry - should be just the one
            foreach (DaggerfallStaticDoors collection in staticDoorCollections)
            {
                if (collection.Doors != null && collection.Doors.Length > 0)
                {
                    foreach (StaticDoor staticDoor in collection.Doors)
                    {
                        if (staticDoor.doorType == DoorTypes.DungeonExit)
                        {
                            // Get static door centre in world space and add to registry
                            Vector3 centre = transform.rotation * staticDoor.buildingMatrix.MultiplyPoint3x4(staticDoor.centre) + transform.position;
                            doorPosRegistry.Add(centre);
                            break;
                        }
                    }
                }
            }

            // Check all action doors against registry and reject if close enough to overlap another door
            foreach (DaggerfallActionDoor actionDoor in actionDoors)
            {
                bool duplicateFound = false;
                foreach (Vector3 pos in doorPosRegistry)
                {
                    if (Vector3.Distance(actionDoor.transform.position, pos) < tolerance)
                    {
                        actionDoor.gameObject.SetActive(false);
                        duplicateFound = true;
                        Debug.Log(">Disabled overlapping action door");
                        break;
                    }
                }
                if (!duplicateFound)
                {
                    doorPosRegistry.Add(actionDoor.transform.position);
                }
            }
        }

        // Finds start and enter markers, should be called with true for starting block, otherwise false to just get water level and castle block data
        private void FindMarkers(DaggerfallRDBBlock dfBlock, ref DFLocation.DungeonBlock block, bool assign)
        {
            if (!dfBlock)
                throw new Exception("DaggerfallDungeon: dfBlock cannot be null.");

            if (dfBlock.StartMarkers != null && dfBlock.StartMarkers.Length > 0)
            {
                // There should only be one start marker per start block
                // This message will let us know if more than one is found
                if (dfBlock.StartMarkers.Length > 1)
                    DaggerfallUnity.LogMessage("DaggerfallDungeon: Multiple 'Start' markers found. Using first marker.", true);

                if (assign)
                    startMarker = dfBlock.StartMarkers[0];

                Billboard dfBillboard = dfBlock.StartMarkers[0].GetComponent<Billboard>();
                block.WaterLevel = dfBillboard.Summary.WaterLevel;
                block.CastleBlock = dfBillboard.Summary.CastleBlock;
            }
            else // No water
                block.WaterLevel = 10000;

            if (dfBlock.EnterMarkers != null && dfBlock.EnterMarkers.Length > 0)
            {

                // There should only be one enter marker per start block
                // This message will let us know if more than one is found
                if (dfBlock.EnterMarkers.Length > 1)
                    DaggerfallUnity.LogMessage("DaggerfallDungeon: Multiple 'Enter' markers found. Using first marker.", true);

                if (assign)
                    enterMarker = dfBlock.EnterMarkers[0];
            }
        }

        // Enumerates all static doors in child blocks
        DaggerfallStaticDoors[] EnumerateStaticDoorCollections()
        {
            return GetComponentsInChildren<DaggerfallStaticDoors>();
        }

        // Enumerates marker positions in dungeon for player to teleport around using quest debugger
        // Not used for any gameplay system and does not collect any other information about marker other than position
        // Currently collects all Enter, Start, Quest, Item markers
        void EnumerateDebuggerMarkers()
        {
            const int editorFlatArchive = 199;
            const int enterMarkerIndex = 8;
            const int startMarkerIndex = 10;
            const int spawnMarkerFlatIndex = 11;
            const int itemMarkerFlatIndex = 18;

            // Only enumerate debugger marker positions once for this dungeon object
            if (debuggerMarkerPositions != null)
                return;
            else
                debuggerMarkerPositions = new List<Vector3>();

            // Step through dungeon layout to find all blocks with markers
            foreach (var dungeonBlock in summary.LocationData.Dungeon.Blocks)
            {
                // Get block data
                DFBlock blockData = DaggerfallUnity.Instance.ContentReader.BlockFileReader.GetBlock(dungeonBlock.BlockName);

                // Iterate all groups
                foreach (DFBlock.RdbObjectRoot group in blockData.RdbBlock.ObjectRootList)
                {
                    // Skip empty object groups
                    if (null == group.RdbObjects)
                        continue;

                    // Look for flats in this group
                    foreach (DFBlock.RdbObject obj in group.RdbObjects)
                    {
                        // Get marker ID
                        ulong markerID = (ulong)(blockData.Position + obj.Position);

                        // Look for editor flats and collect marker positions adjusted for dungeon block position
                        Vector3 position = new Vector3(obj.XPos, -obj.YPos, obj.ZPos) * MeshReader.GlobalScale;
                        if (obj.Type == DFBlock.RdbResourceTypes.Flat)
                        {
                            if (obj.Resources.FlatResource.TextureArchive == editorFlatArchive)
                            {
                                Vector3 dungeonBlockPosition = new Vector3(dungeonBlock.X * RDBLayout.RDBSide, 0, dungeonBlock.Z * RDBLayout.RDBSide);
                                switch (obj.Resources.FlatResource.TextureRecord)
                                {
                                    case enterMarkerIndex:
                                    case startMarkerIndex:
                                    case itemMarkerFlatIndex:
                                    case spawnMarkerFlatIndex:
                                        debuggerMarkerPositions.Add(dungeonBlockPosition + position);
                                        break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private bool ReadyCheck()
        {
            // Ensure we have a DaggerfallUnity reference
            if (dfUnity == null)
            {
                dfUnity = DaggerfallUnity.Instance;
            }

            // Do nothing if DaggerfallUnity not ready
            if (!dfUnity.IsReady)
            {
                DaggerfallUnity.LogMessage("DaggerfallDungeon: DaggerfallUnity component is not ready. Have you set your Arena2 path?");
                return false;
            }

            return true;
        }

        #endregion

        /// <summary>
        /// An event raised after a dungeon has been set and its layout has been performed.
        /// </summary>
        public static event Action<DaggerfallDungeon> OnSetDungeon;
        private void RaiseOnSetDungeonEvent()
        {
            if (OnSetDungeon != null)
                OnSetDungeon(this);
        }
    }
}
