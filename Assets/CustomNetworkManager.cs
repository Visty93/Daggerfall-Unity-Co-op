using Mirror;
using UnityEngine;

public class CustomNetworkManager : NetworkManager
{
    public override void OnServerDisconnect(NetworkConnection conn)
    {
        Debug.Log($"[CustomNetworkManager] Client disconnected: {conn.connectionId}");

        // Reclaim authority of all owned objects before the client fully disconnects
        if (conn.clientOwnedObjects != null)
        {
            foreach (NetworkIdentity ownedObject in conn.clientOwnedObjects)
            {
                if (ownedObject != null)
                {
                    Debug.Log($"[CustomNetworkManager] Reclaiming authority for object: {ownedObject.name} (NetID: {ownedObject.netId})");
                    ownedObject.RemoveClientAuthority();
                }
            }
        }

        // Call the base class method to handle the normal disconnect process
        base.OnServerDisconnect(conn);
        Debug.Log($"[CustomNetworkManager] Base disconnect logic executed for client: {conn.connectionId}");
    }
}
