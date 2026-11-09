// Examples of moving-frame support in the Multiplayer API v0.5.
// Calls here do NOT replace the original mod's spawning or its network transform.
using System;
using UnityEngine;
using DaggerfallCoop.API;

public sealed class MultiplayerAPIMovingFramesExample : MonoBehaviour
{
    // Each handle belongs to this adapter and must be disposed when its object exits.
    ModMovingFrameHandle boatEnemy;
    ModMovingFrameHandle wagonPassenger;
    ModMovingFrameHandle unnetworkedRoot;

    // Call only after the enemy is a registered co-op network enemy and a
    // specific boat's deck has been detected under it. The API will request
    // temporary ownership, and carry ONLY its authoritative root. Observers
    // see the usual enemy network transform; no hierarchy changes occur.
    public void EnemyBoarded(GameObject networkEnemy, Transform boatMesh, Collider deck)
    {
        ReleaseEnemy();
        boatEnemy = MultiplayerAPI.FollowEnemyOnMovingFrame(networkEnemy, boatMesh, deck,
            new ModMovingFrameOptions { MaxStepMetres = 8f, LostSupportGraceSeconds = .12f,
                RequestEnemyOwnership = true });
    }
    public void ReleaseEnemy() { if (boatEnemy != null) boatEnemy.Dispose(); boatEnemy = null; }

    // Call on the REMOTE wagon replica, once it has a working solid collider.
    // The local player will be carried only while standing on that collider.
    // Do not simultaneously enable any other passenger-support adapter.
    public void WagonReplicaReady(Transform visualRoot, Collider standingFloor)
    {
        ReleaseWagon();
        wagonPassenger = MultiplayerAPI.RegisterLocalPlayerMovingSurface(standingFloor, visualRoot,
            new ModMovingFrameOptions { MaxStepMetres = 3f });
    }
    public void ReleaseWagon() { if (wagonPassenger != null) wagonPassenger.Dispose(); wagonPassenger = null; }

    // Generic independent root following (no ownership pin or support probe).
    // For native networked roots, only current Mirror authority moves the actor.
    public void FollowLocallySimulatedProp(Transform root, Transform movingFrame)
    {
        if (unnetworkedRoot != null) unnetworkedRoot.Dispose();
        unnetworkedRoot = MultiplayerAPI.FollowMovingFrame(root, movingFrame,
            new ModMovingFrameOptions { MaxStepMetres = 8f });
    }
    void OnDestroy()
    {
        ReleaseEnemy(); ReleaseWagon();
        if (unnetworkedRoot != null) unnetworkedRoot.Dispose();
    }
}
