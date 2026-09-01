using System.Collections;
using Mirror;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;


[RequireComponent(typeof(NetworkIdentity))]
public class DaggerfallInteriorNetwork : NetworkBehaviour
{
    [SyncVar] public string regionName;
    [SyncVar] public string locationName;
    [SyncVar] public int buildingKey;
	[SyncVar] public int doorRecordIndex;
[SyncVar] public int doorIndex; // 🔴 <-- Add this
    [SyncVar] public float posX;
    [SyncVar] public float posY; // ✅ NEW
    [SyncVar] public float posZ;

    [SyncVar] public Vector3 offset;      // From door.buildingMatrix.GetColumn(3)
    [SyncVar] public Quaternion rotation; // From buildingMatrix
	[SyncVar] public uint doorOwnerNetId;

	
	// SyncVars to recreate StaticDoor on client
[SyncVar] public Vector3 doorPosition;
[SyncVar] public Vector3 doorNormal;
[SyncVar] public Vector3 doorOwnerPosition;
[SyncVar] public Quaternion doorOwnerRotation;
[SyncVar] public Vector3 buildingMatrixOffset;
[SyncVar] public Quaternion buildingMatrixRotation;
[SyncVar] public string blockName;
[SyncVar] public int blockIndex;
[SyncVar] public int regionIndex;
[SyncVar] public int locationIndex;


	

	  PlayerGPS playerGPS;
	  public StaticDoor realClickedDoor;

    // Not synced directly

	private Transform doorOwner;
    public StaticDoor staticDoor;
	private DaggerfallInterior interior;
    public ClimateBases climateBase = ClimateBases.Temperate;
    public PlayerGPS.DiscoveredBuilding discoveredBuilding;
public Transform originalDoorOwner;
    private bool isSet = false;
	public bool IsReady { get; private set; } = false;



[System.Serializable]
public struct InteriorNetworkData
{
    public string regionName;
    public string locationName;
    public int regionIndex;
    public int locationIndex;
    public int buildingKey;
    public float posX, posY, posZ;
    public int recordIndex;
    public int doorIndex;
    public int blockIndex;
    public Vector3 doorPosition;
    public Vector3 doorNormal;
    public Vector3 doorOwnerPosition;
    public Quaternion doorOwnerRotation;
    public Matrix4x4 buildingMatrix;
    public string blockName;
    public ClimateBases climate;
	public PlayerGPS.DiscoveredBuilding discoveredBuilding;
}




/// <summary>
/// Call this from host after instantiation but before Spawn().
/// </summary>
public void SetInteriorData(
    string region,
    string location,
    int key,
    float x, float y, float z,
    StaticDoor door,
    ClimateBases climate,
    PlayerGPS.DiscoveredBuilding buildingData,
    Transform doorOwnerTransform)
{
    this.regionName = region;
    this.locationName = location;
    this.buildingKey = key;
    this.posX = x;
    this.posY = y;
    this.posZ = z;
    this.climateBase = climate;
    this.discoveredBuilding = buildingData;
    this.doorOwner = doorOwnerTransform;
    this.staticDoor = door;
	this.blockIndex = door.blockIndex;
	this.originalDoorOwner = doorOwnerTransform;


    // Door metadata
    this.doorRecordIndex = door.recordIndex;
    this.doorIndex = door.doorIndex;
    this.doorPosition = door.centre;
    this.doorNormal = door.normal;
    this.doorOwnerPosition = door.ownerPosition;
    this.doorOwnerRotation = door.ownerRotation;
    this.buildingMatrixOffset = door.buildingMatrix.GetColumn(3);
    this.buildingMatrixRotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

    if (doorOwnerTransform != null)
    {
        blockName = doorOwnerTransform.name;
        var identity = doorOwnerTransform.GetComponent<NetworkIdentity>();
        if (identity != null)
            doorOwnerNetId = identity.netId;
    }
    else
    {
        Debug.LogWarning("[InteriorNet] ⚠️ doorOwnerTransform was null on host.");
        blockName = "";
    }

    IsReady = true;
}




public InteriorNetworkData CreateNetworkData()
{
    PlayerGPS gps = GameManager.Instance.PlayerGPS;

    return new InteriorNetworkData
    {
        regionName = regionName,
        locationName = locationName,
        regionIndex = gps.CurrentRegionIndex,
        locationIndex = gps.CurrentLocationIndex,
        buildingKey = buildingKey,
        posX = posX,
        posY = posY,
        posZ = posZ,
        recordIndex = staticDoor.recordIndex,
        doorIndex = staticDoor.doorIndex,
        blockIndex = staticDoor.blockIndex,
        doorPosition = staticDoor.centre,
        doorNormal = staticDoor.normal,
        doorOwnerPosition = staticDoor.ownerPosition,
        doorOwnerRotation = staticDoor.ownerRotation,
        buildingMatrix = staticDoor.buildingMatrix,
        climate = climateBase,
        blockName = blockName,
		discoveredBuilding = this.discoveredBuilding, // ✅ THIS is the key line
    };
}







public static Transform FindExteriorDoorOwner(int buildingKey, string locationName)
{
    Debug.Log($"[InteriorNet] 🧭 Fallback search for doorOwner using buildingKey={buildingKey} and location={locationName}");

    DaggerfallStaticDoors[] allDoors = FindObjectsOfType<DaggerfallStaticDoors>();

    foreach (DaggerfallStaticDoors doorSet in allDoors)
    {
        foreach (StaticDoor door in doorSet.Doors)
        {
            if (door.buildingKey == buildingKey)
            {
                Debug.Log($"[InteriorNet] ✅ Fallback found doorOwner={doorSet.gameObject.name}");
                return doorSet.transform;
            }
        }

        // Optional loose match fallback
        if (doorSet.gameObject.name.Contains(locationName))
        {
            Debug.LogWarning($"[InteriorNet] ⚠️ Weak fallback found by name: {doorSet.gameObject.name}");
            return doorSet.transform;
        }
    }

    Debug.LogError("[InteriorNet] ❌ Fallback failed to find matching doorOwner.");
    return null;
}





public override void OnStartClient()
{
    base.OnStartClient();
    StartCoroutine(CheckInteriorReady());
}

private IEnumerator CheckInteriorReady()
{
    yield return new WaitForSeconds(0.5f); // Wait for identity resolution
    if (!IsReady)
    {
        Debug.Log("[InteriorNet] ⏳ Delayed interior readiness check (OnStartClient)");
        PlayerMultiplayer player = NetworkClient.connection.identity.GetComponent<PlayerMultiplayer>();
        if (player != null)
            player.CmdRequestInteriorData(netId);
    }
}




[TargetRpc]
public void TargetSendInteriorData(NetworkConnection target, InteriorNetworkData data)
{
    Debug.Log($"[InteriorNet] ✅ Received interior data from host on conn={target.connectionId}");

    // ✅ Apply discovery data before layout
    this.discoveredBuilding = data.discoveredBuilding;

    StartCoroutine(DeferredSpawnInterior(data));
}



public IEnumerator DeferredSpawnInterior(InteriorNetworkData data)
{
    Debug.Log($"[InteriorNet] 🛠 DeferredSpawnInterior() started, buildingKey={data.buildingKey}");

    // 1. Create StaticDoor from network data (used for DoLayout)
StaticDoor door = new StaticDoor
{
    buildingKey = data.buildingKey,
    recordIndex = data.recordIndex,
    doorIndex = data.doorIndex,
    blockIndex = data.blockIndex, // ✅ THIS was missing!
    centre = data.doorPosition,
    normal = data.doorNormal,
    ownerPosition = data.doorOwnerPosition,
    ownerRotation = data.doorOwnerRotation,
    buildingMatrix = data.buildingMatrix,
};
staticDoor = door;
realClickedDoor = door;

    // 2. Reconstruct or find doorOwner (DaggerfallBlock)
    string cleanBlockName = data.blockName;
    if (cleanBlockName.StartsWith("DaggerfallBlock [") && cleanBlockName.EndsWith("]"))
        cleanBlockName = cleanBlockName.Substring(17, cleanBlockName.Length - 18); // trim prefix & suffix

    GameObject doorOwnerGO = GameObject.Find($"DaggerfallBlock [{cleanBlockName}]");
    DFBlock dfBlock;

    if (!RMBLayout.GetBlockData(cleanBlockName, out dfBlock))
    {
        Debug.LogError($"[InteriorNet] ❌ Failed to load DFBlock from disk: {cleanBlockName}");
		Debug.Log($"[InteriorNet] Block {cleanBlockName} has {dfBlock.RmbBlock.SubRecords.Length} records");
        yield break;
    }

    if (!doorOwnerGO)
    {
        doorOwnerGO = new GameObject($"DaggerfallBlock [{cleanBlockName}]");
        var staticDoors = doorOwnerGO.AddComponent<DaggerfallStaticDoors>();
		Debug.Log($"[InteriorNet] ✅ Added DaggerfallStaticDoors to dummy: {doorOwnerGO.name}");

        List<StaticDoor> doors = new List<StaticDoor>();
        for (int i = 0; i < dfBlock.RmbBlock.SubRecords.Length; i++)
        {
            var sub = dfBlock.RmbBlock.SubRecords[i];
            if (sub.Exterior.BlockDoorRecords == null) continue;

            for (int j = 0; j < sub.Exterior.BlockDoorRecords.Length; j++)
            {
                var dr = sub.Exterior.BlockDoorRecords[j];
                StaticDoor extDoor = new StaticDoor
                {
                    buildingKey = dfBlock.Index,
                    recordIndex = i,
                    doorIndex = j,
                    blockIndex = dfBlock.Index,
                    centre = new Vector3(dr.XPos, -dr.YPos, dr.ZPos) * MeshReader.GlobalScale,
                    normal = Quaternion.Euler(0, -dr.YRotation / (float)BlocksFile.RotationDivisor, 0) * Vector3.forward
                };
                doors.Add(extDoor);
            }
        }

        staticDoors.Doors = doors.ToArray();
        Debug.Log($"[InteriorNet] ✅ Created dummy doorOwner with {doors.Count} exterior doors");
    }

    //door.blockIndex = dfBlock.Index;
    doorOwner = doorOwnerGO.transform;

    // 3. Validate interior
    interior = GetComponent<DaggerfallInterior>();
    if (!interior)
    {
        Debug.LogError("[InteriorNet] ❌ Missing DaggerfallInterior component!");
        yield break;
    }

    // 4. Zero transform for layout
    Vector3 originalPos = transform.position;
    Quaternion originalRot = transform.rotation;
    transform.position = Vector3.zero;
    transform.rotation = Quaternion.identity;

    // 5. Inject block data before layout
    if (!InjectBlockData(interior, data.blockName, data.recordIndex))
    {
        Debug.LogError("[InteriorNet] ❌ Failed to inject block data.");
        yield break;
    }

    // 6. Run DoLayout using remote location
    try
    {
        DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(data.regionIndex, data.locationIndex);
		if (data.recordIndex >= dfBlock.RmbBlock.SubRecords.Length)
{
    Debug.LogError($"[InteriorNet] ❌ Invalid recordIndex: {data.recordIndex} >= {dfBlock.RmbBlock.SubRecords.Length} for block {cleanBlockName}!");
    yield break;
}
        bool success = interior.DoLayoutMultiplayer(doorOwner, staticDoor, data.climate, discoveredBuilding, location);

        if (!success)
        {
            Debug.LogError("[InteriorNet] ❌ DoLayoutMultiplayer() failed.");
            yield break;
        }

        Debug.Log("[InteriorNet] ✅ DoLayout complete.");
		if (interior.EntryDoor.buildingKey != 0)
{
    GameObjectHelper.AddQuestResourceObjects(SiteTypes.Building, interior.transform, interior.EntryDoor.buildingKey);
    Debug.Log($"[InteriorNet] ✅ Added quest resource objects for buildingKey={interior.EntryDoor.buildingKey}");
}
    }
    catch (Exception ex)
    {
        Debug.LogError("[InteriorNet] ❌ Exception during DoLayout: " + ex);
        yield break;
    }
	
	// ✅ 6.5. Manually assign exterior doors to interior
	// 🔁 Manually assign ExteriorDoors since DoLayoutMultiplayer doesn't do it
/*DaggerfallStaticDoors layoutDoors = doorOwner.GetComponent<DaggerfallStaticDoors>();
if (layoutDoors)
{
    DaggerfallStaticDoors interiorDoors = interior.GetComponent<DaggerfallStaticDoors>();
    if (!interiorDoors)
        interiorDoors = interior.gameObject.AddComponent<DaggerfallStaticDoors>();

    interiorDoors.Doors = layoutDoors.Doors.ToArray();
    interior.ExteriorDoors = interiorDoors;

    Debug.Log($"[InteriorNet] ✅ Assigned {layoutDoors.Doors.Length} exterior doors to interior");
}
else
{
    Debug.LogWarning("[InteriorNet] ⚠️ No layoutDoors found on dummy doorOwner!");
}

	originalDoorOwner = PlayerEnterExit.realSceneDoorOwner ?? doorOwner;*/
	

    // 7. Restore transform
    transform.position = originalPos;
    transform.rotation = originalRot;

  /*  // 8. Assign interior doors generated by layout
    var interiorDoors = GetComponent<DaggerfallStaticDoors>() ?? gameObject.AddComponent<DaggerfallStaticDoors>();
    DaggerfallStaticDoors layoutDoors = doorOwner.GetComponent<DaggerfallStaticDoors>();
    if (layoutDoors != null)
    {
        interiorDoors.Doors = layoutDoors.Doors.ToArray(); // usually gets updated by layout
        GameManager.Instance.PlayerEnterExit.SetExteriorDoors(interiorDoors.Doors);
    }
    else
    {
        Debug.LogWarning("[InteriorNet] ⚠️ No DaggerfallStaticDoors found on doorOwner after layout.");
    }*/

    // 9. Move underground and align
    Vector3 layoutPos = door.ownerPosition + (Vector3)door.buildingMatrix.GetColumn(3);
    layoutPos += GameManager.Instance.StreamingWorld.WorldCompensation;
    transform.position = new Vector3(layoutPos.x, layoutPos.y - 200f, layoutPos.z);
    transform.rotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

    // 10. Done
    IsReady = true;
    Debug.Log("[InteriorNet] ✅ DeferredSpawnInterior complete");
	NetworkedInteriorRegistry.Register(this);
    yield return null;
}






/// <summary>
/// Reconstruct a “doorOwner” GameObject (with a DaggerfallStaticDoors component) from block data.
/// This method uses the blockName (sent by the host) to load the DFBlock from disk and then extract door records.
/// </summary>
public Transform RecreateFullDoorOwnerFromBlock(string rawBlockName, int buildingKey)
{
    if (string.IsNullOrEmpty(rawBlockName))
    {
        Debug.LogError("[InteriorNet] ❌ Cannot recreate doorOwner: blockName is null or empty.");
        return null;
    }

    // Extract just the RMB name if wrapped
    if (rawBlockName.Contains("[") && rawBlockName.Contains("]"))
    {
        int start = rawBlockName.IndexOf("[") + 1;
        int end = rawBlockName.IndexOf("]");
        rawBlockName = rawBlockName.Substring(start, end - start).Trim();
        Debug.Log($"[InteriorNet] ✂ Extracted raw block name: {rawBlockName}");
    }

    if (!RMBLayout.GetBlockData(rawBlockName, out DFBlock dfBlock))
    {
        Debug.LogError($"[InteriorNet] ❌ Failed to load block data for block: {rawBlockName}");
        return null;
    }

    List<StaticDoor> doors = new List<StaticDoor>();

    for (int i = 0; i < dfBlock.RmbBlock.SubRecords.Length; i++)
    {
        var sub = dfBlock.RmbBlock.SubRecords[i];
        if (sub.Exterior.BlockDoorRecords == null || sub.Exterior.BlockDoorRecords.Length == 0)
        {
            Debug.Log($"[InteriorNet] ⚠ SubRecord {i} has no doors.");
            continue;
        }

        for (int j = 0; j < sub.Exterior.BlockDoorRecords.Length; j++)
        {
            var dr = sub.Exterior.BlockDoorRecords[j];
            StaticDoor d = new StaticDoor
            {
                buildingKey = buildingKey, // ✅ Use actual key passed in
                recordIndex = i,
                doorIndex = j,
                blockIndex = this.blockIndex, // ✅ Use synced value from host instead of dfBlock.Index
                centre = new Vector3(dr.XPos, -dr.YPos, dr.ZPos) * MeshReader.GlobalScale,
                normal = Quaternion.Euler(0, -dr.YRotation / (float)BlocksFile.RotationDivisor, 0) * Vector3.forward
            };
            doors.Add(d);
        }
    }

    if (doors.Count == 0)
    {
        Debug.LogError($"[InteriorNet] ❌ No doors found in block: {rawBlockName} after full scan.");
        return null;
    }

    GameObject dummy = new GameObject($"DummyDoorOwner_{rawBlockName}");
    DaggerfallStaticDoors staticDoors = dummy.AddComponent<DaggerfallStaticDoors>();
    staticDoors.Doors = doors.ToArray();

    Debug.Log($"[InteriorNet] ✅ Created dummy doorOwner with {doors.Count} doors for block: {rawBlockName}");
    return dummy.transform;
}


public bool InjectBlockData(DaggerfallInterior interior, string blockName, int recordIndex)
{
    if (string.IsNullOrEmpty(blockName))
    {
        Debug.LogError("[InteriorNet] ❌ Block name is null or empty.");
        return false;
    }

    Debug.Log($"[InteriorNet] 🔍 Attempting to load block data: {blockName}");

    // Clean input: remove "DaggerfallBlock [XYZ]" wrappers if present
    string cleanName = blockName;
    if (cleanName.Contains("[") && cleanName.Contains("]"))
    {
        int start = cleanName.IndexOf("[") + 1;
        int end = cleanName.IndexOf("]");
        cleanName = cleanName.Substring(start, end - start).Trim();
        Debug.Log($"[InteriorNet] ✂ Cleaned wrapped block name: {cleanName}");
    }

    // Try to load block data
    if (!RMBLayout.GetBlockData(cleanName, out DFBlock block))
    {
        // If that fails, try stripping any extension
        string strippedName = Path.GetFileNameWithoutExtension(cleanName);
        Debug.LogWarning($"[InteriorNet] ⚠️ Failed to load with cleaned name, trying stripped: {strippedName}");

        if (!RMBLayout.GetBlockData(strippedName, out block))
        {
            Debug.LogError($"[InteriorNet] ❌ Could not load block data: {blockName}, {cleanName}, or {strippedName}");
            return false;
        }
        else
        {
            Debug.Log($"[InteriorNet] ✅ Loaded block data using stripped name: {strippedName}");
        }
    }
    else
    {
        Debug.Log($"[InteriorNet] ✅ Loaded block data using cleaned name: {cleanName}");
    }

    // Sanity check recordIndex
    if (block.RmbBlock.SubRecords == null || recordIndex < 0 || recordIndex >= block.RmbBlock.SubRecords.Length)
    {
        Debug.LogError($"[InteriorNet] ❌ Invalid record index {recordIndex} for block {cleanName}");
        return false;
    }

    // Inject private fields via reflection
    typeof(DaggerfallInterior)
        .GetField("blockData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.SetValue(interior, block);

    var record = block.RmbBlock.SubRecords[recordIndex];
    typeof(DaggerfallInterior)
        .GetField("recordData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.SetValue(interior, record);

    Debug.Log($"[InteriorNet] ✅ Injected block data: {cleanName}, recordIndex={recordIndex}");
    return true;
}


	

/*

public IEnumerator DeferredSpawnInteriorHost(InteriorNetworkData data)
{
    Debug.Log($"[InteriorNet] 🛠 Host DeferredSpawnInterior() for buildingKey={data.buildingKey}");

    StaticDoor door = new StaticDoor
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
    staticDoor = door;

    // Reconstruct doorOwner
    Transform doorOwnerTransform = RecreateFullDoorOwnerFromBlock(data.blockName, data.buildingKey);
    if (!doorOwnerTransform)
    {
        Debug.LogError("[InteriorNet] ❌ Host failed to recreate doorOwner");
        yield break;
    }

    this.doorOwner = doorOwnerTransform;

    // Run DoLayout
    interior = GetComponent<DaggerfallInterior>();
    if (!InjectBlockData(interior, data.blockName, data.recordIndex))
    {
        Debug.LogError("[InteriorNet] ❌ Host failed to inject block data");
        yield break;
    }

    DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(data.regionIndex, data.locationIndex);

    bool success = interior.DoLayoutMultiplayer(doorOwner, staticDoor, data.climate, discoveredBuilding, location);
    if (!success)
    {
        Debug.LogError("[InteriorNet] ❌ Host DoLayoutMultiplayer failed");
        yield break;
    }

    // Align and move underground
    Vector3 layoutPos = door.ownerPosition + (Vector3)door.buildingMatrix.GetColumn(3);
    layoutPos += GameManager.Instance.StreamingWorld.WorldCompensation;
    transform.position = new Vector3(layoutPos.x, layoutPos.y - 200f, layoutPos.z);
    transform.rotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

    IsReady = true;
    Debug.Log("[InteriorNet] ✅ Host DeferredSpawnInterior complete");

    yield return null;
}
*/











/*
[TargetRpc]
public void TargetEnterInterior(NetworkConnection target, InteriorNetworkData data)
{
    Debug.Log("[TargetEnterInterior] Called on client.");
    StartCoroutine(DeferredSpawnInterior(data));
}

*/



/*

private IEnumerator DelayedClientInteriorSpawn(InteriorNetworkData data)
{
    yield return null;

    // Wait for prefab
    DaggerfallInteriorNetwork found = null;
    for (int i = 0; i < 100; i++)
    {
        found = FindObjectsOfType<DaggerfallInteriorNetwork>()
            .FirstOrDefault(x => x.buildingKey == data.buildingKey &&
                                 x.regionName == data.regionName &&
                                 x.locationName == data.locationName);
        if (found != null)
            break;

        yield return new WaitForSeconds(0.05f);
    }

    if (found == null)
    {
        Debug.LogError("[DelayedClientInteriorSpawn] Could not find networked interior.");
        yield break;
    }

    // Build the missing door manually
    StaticDoor door = new StaticDoor()
    {
        buildingKey     = data.buildingKey,
        centre          = data.doorPosition,
        normal          = data.doorNormal,
        ownerPosition   = data.doorOwnerPosition,
        ownerRotation   = data.doorOwnerRotation,
        recordIndex     = data.recordIndex,
        doorType        = DoorTypes.Building,
        buildingMatrix  = data.buildingMatrix
    };

    // Build dummy door owner object
    GameObject doorOwnerObj = new GameObject("DoorOwner_" + data.buildingKey);
    doorOwnerObj.transform.position = data.doorOwnerPosition;
    doorOwnerObj.transform.rotation = data.doorOwnerRotation;
    Transform doorOwner = doorOwnerObj.transform;

    // 🧱 Get interior component
    DaggerfallInterior interior = found.GetComponent<DaggerfallInterior>();
    if (!interior)
    {
        Debug.LogError("[ClientInteriorSpawn] No DaggerfallInterior component found.");
        yield break;
    }

    // ✅ Call SetInteriorData BEFORE DoLayout
    found.SetInteriorData(
        data.regionName,
        data.locationName,
        data.buildingKey,
        data.posX,
        data.posY,
        data.posZ,
        door,
        data.climate,
        data.discoveredBuilding,
        doorOwner
    );

    // 🧩 Then call DoLayout
    interior.DoLayout(doorOwner, door, data.climate, data.discoveredBuilding);
	GameManager.Instance.PlayerEnterExit.interior = interior;
    Debug.Log($"[ClientInteriorSpawn] Interior generated for {data.locationName} key={data.buildingKey}");
}
*/

/*
private Transform FindExteriorDoorOwner(int recordIndex)
{
    DaggerfallStaticDoors[] allDoors = GameObject.FindObjectsOfType<DaggerfallStaticDoors>();
    foreach (var doors in allDoors)
    {
        foreach (var door in doors.Doors)
        {
            if (door.recordIndex == recordIndex)
                return doors.transform;
        }
    }
    return null;
}*/


/*
public override void OnStartClient()
{
    base.OnStartClient();

    if (!isServer)
    {
        Debug.Log("[InteriorNet] OnStartClient: rebuilding interior from SyncVars");
        StartCoroutine(DeferredSpawnInterior()); // no param version
    }
}*/


/*
private IEnumerator DeferredSpawnInterior()
{
    Debug.Log("[InteriorNet] Starting client DeferredSpawnInterior()");

    // Wait for SyncVars to sync
    int retries = 0;
    while (doorRecordIndex == 0 && retries < 100)
    {
        yield return new WaitForSeconds(0.05f);
        retries++;
    }

    if (doorRecordIndex == 0)
    {
        Debug.LogError("[InteriorNet] Timeout waiting for SyncVars.");
        yield break;
    }

    // Rebuild StaticDoor from SyncVars
    StaticDoor door = RebuildStaticDoorFromSyncVars();
    staticDoor = door;

    // Setup door owner
    doorOwner = new GameObject("DoorOwner_" + buildingKey).transform;
    doorOwner.position = doorOwnerPosition;
    doorOwner.rotation = doorOwnerRotation;

// Reuse existing interior component on this prefab
interior = GetComponent<DaggerfallInterior>();
if (interior == null)
{
    Debug.LogError("[InteriorNet] No DaggerfallInterior on prefab.");
    yield break;
}



// Layout the interior
try
{
    interior.DoLayout(doorOwner, door, climateBase, discoveredBuilding);
}
catch (Exception ex)
{
    Debug.LogError("[InteriorNet] DoLayout failed: " + ex);
    yield break;
}

    // Position prefab underground
    Vector3 layoutPos = door.ownerPosition + (Vector3)door.buildingMatrix.GetColumn(3);
    Vector3 offset = GameManager.Instance.StreamingWorld.WorldCompensation;
    layoutPos += new Vector3(offset.x, 0f, offset.z);
    transform.position = new Vector3(layoutPos.x, layoutPos.y - 200f, layoutPos.z);
    transform.rotation = GameObjectHelper.QuaternionFromMatrix(door.buildingMatrix);

    IsReady = true;
    Debug.Log("[InteriorNet] Client DeferredSpawnInterior complete.");
}




public StaticDoor RebuildStaticDoorFromSyncVars()
{
    return new StaticDoor
    {
        buildingKey = buildingKey,
        recordIndex = doorRecordIndex,
        doorIndex = doorIndex,
        centre = doorPosition,
        normal = doorNormal,
        ownerPosition = doorOwnerPosition,
        ownerRotation = doorOwnerRotation,
        buildingMatrix = Matrix4x4.TRS(
            buildingMatrixOffset,
            buildingMatrixRotation,
            Vector3.one
        )
    };
}
*/








/*[ClientRpc]
public void RpcInitializeClientInterior(
    string region,
    string location,
    int key,
    float x,
    float y,
    float z,
    int recordIndex,
    Vector3 position,
    Vector3 normal,
    Vector3 ownerPos,
    Quaternion ownerRot,
    Vector3 matrixOffset,
    Quaternion matrixRot)
{
    regionName = region;
    locationName = location;
    buildingKey = key;
    posX = x;
    posY = y;
    posZ = z;

    staticDoor = new StaticDoor
    {
        buildingKey     = key,
        recordIndex     = recordIndex,
        doorType        = DoorTypes.Building,
        centre          = position,
        normal          = normal,
        ownerPosition   = ownerPos,
        ownerRotation   = ownerRot,
        buildingMatrix  = Matrix4x4.TRS(matrixOffset, matrixRot, Vector3.one)
    };

    offset = matrixOffset;
    rotation = matrixRot;

    doorRecordIndex = recordIndex;
    doorPosition = position;
    doorNormal = normal;
    doorOwnerPosition = ownerPos;
    doorOwnerRotation = ownerRot;
    buildingMatrixOffset = matrixOffset;
    buildingMatrixRotation = matrixRot;

    // Reconstruct doorOwner
    doorOwner = FindExteriorDoorOwner(recordIndex);

    StartCoroutine(DeferredSpawnInterior(data));
}*/



}
// === Add this at the very end of DaggerfallInteriorNetwork.cs ===
public static class NetworkedInteriorRegistry
{
    private static Dictionary<int, DaggerfallInteriorNetwork> interiorsByKey = new Dictionary<int, DaggerfallInteriorNetwork>();

    public static bool TryGet(int key, out DaggerfallInteriorNetwork net)
        => interiorsByKey.TryGetValue(key, out net);

    public static void Register(DaggerfallInteriorNetwork net)
    {
        if (!interiorsByKey.ContainsKey(net.buildingKey))
        {
            interiorsByKey[net.buildingKey] = net;
            Debug.Log($"[InteriorNet] ✅ Registered interior {net.buildingKey}");
        }
    }
}
