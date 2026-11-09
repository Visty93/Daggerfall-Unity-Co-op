// Example only. The API does not load prefabs or execute mod gameplay for you.
using System;
using System.Collections.Generic;
using DaggerfallCoop.API;

public static class MultiplayerAPIStateExample
{
    const string ModGuid="example.world.mod";
    static readonly Dictionary<string,ModStateRecord> replicas=new Dictionary<string,ModStateRecord>();

    public static void Register()
    {
        MultiplayerAPI.SubscribeState(ModGuid,OnChange,OnSnapshotComplete);
        MultiplayerAPI.SessionEnded+=OnSessionEnded;
    }
    public static void Unregister()
    {
        MultiplayerAPI.UnsubscribeState(ModGuid);
        MultiplayerAPI.SessionEnded-=OnSessionEnded;
        OnSessionEnded();
    }
    // Call only after the server has checked the authenticated request sender,
    // location, ownership and the mod's spawn/party/interaction rules.
    public static bool PublishOnServer(string stableKey,byte[] description,uint owner)
    {
        ulong revision;
        return MultiplayerAPI.SetState(ModGuid,stableKey,description,owner,0,out revision);
        // expectedRevision=0 means create-only: an existing key wins.
        // Use TryGetState and its Revision for updates/owner transfers/removals.
    }
    static void OnChange(ModStateChange change)
    {
        if(change.Removed)
        {
            replicas.Remove(change.Record.Key);
            // Destroy local replica, if any.
            return;
        }
        replicas[change.Record.Key]=change.Record;
        // Idempotently create/update your own local prefab, colliders and scripts.
        // Payload contains your mod's data, never a foreign GameObject reference.
        // Treat snapshot records as current descriptions, not "spawn again" commands.
    }
    static void OnSnapshotComplete(ModStateSnapshot snapshot)
    {
        var current=new HashSet<string>(snapshot.Keys);
        foreach(var key in new List<string>(replicas.Keys))
            if(!current.Contains(key))
            {
                replicas.Remove(key);
                // Destroy stale local replica.
            }
    }
    static void OnSessionEnded()
    {
        replicas.Clear();
        // Destroy local replicas and reset per-session state.
    }
    public static void AfterModRestorationSettles()
    {
        MultiplayerAPI.RequestStateSnapshot(ModGuid);
        // The server's original mod objects may ALSO need rediscovery/rebinding.
        // A snapshot alone cannot reconcile their native save state.
    }
}
