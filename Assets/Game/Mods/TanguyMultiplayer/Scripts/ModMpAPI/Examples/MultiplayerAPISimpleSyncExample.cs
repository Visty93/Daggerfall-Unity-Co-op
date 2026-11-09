// Example: registering ordinary world GameObjects without Mirror code.
// Register the same visual kinds on every co-op peer (including a headless server).
// Visual prefabs should contain ordinary mesh/sprite components and colliders,
// but NO gameplay scripts that would independently run on receiving replicas.
using System;
using UnityEngine;
using DaggerfallCoop.API;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;

public sealed class SimpleModSyncExample : MonoBehaviour
{
    // Assign these to your own mod's local prefab assets.
    public GameObject MemorialRosePrefab;
    public GameObject MovingWagonPrefab;
    ModSyncWorld world;

    void Start()
    {
        // Use a stable, UNIQUE ID for your mod, shared on all peers.
        world = MultiplayerAPI.OpenSyncWorld("example.mod.roses-and-wagons");
        world.RegisterPrefab("rose", MemorialRosePrefab, OnRosePickedUp, ApplyRoseState);
        world.RegisterPrefab("wagon", MovingWagonPrefab);
    }

    // Call when your single-player gameplay creates a rose and MP is ready.
    public void RegisterRose(GameObject existingRose)
    {
        if (world == null || !world.Ready || existingRose == null) return;
        existingRose.AddComponent<ModSyncMarker>().Configure(world,
            "rose/" + Guid.NewGuid().ToString("N"), "rose",
            new ModSyncOptions {
                Motion = ModWorldMotion.Fixed,
                Flags = ModSyncFlags.Pickable | ModSyncFlags.AutoPickupOnClick |
                        ModSyncFlags.KeepOriginalParent,
                LifetimeSeconds = 1800f,
            }, "red");
    }

    // A moving platform can be registered with a single method as well.
    public void RegisterWagon(GameObject wagon)
    {
        if (world == null || !world.Ready || wagon == null) return;
        world.Register(wagon, "wagon/" + Guid.NewGuid().ToString("N"), "wagon",
            new ModSyncOptions {
                Motion = ModWorldMotion.Smooth,
                Flags = ModSyncFlags.CarryLocalPlayer | ModSyncFlags.KeepOriginalParent,
                VisibilityMetres = 300f,
                LifetimeSeconds = 0,
            });
        // On the receiver's prefab, add ModSyncCarrySurface to the deck collider.
        // The original source wagon's movement remains the mod's responsibility.
    }

    static void ApplyRoseState(GameObject replica, ModWorldRecord record, string payload)
    {
        // Apply the mod's visual variation or state to this local replica.
        // No gameplay events or inventory changes should run from this callback.
        replica.name = "Memorial Rose (" + payload + ")";
    }

    static void OnRosePickedUp(ModWorldRecord record)
    {
        // Called only on the peer that successfully took the world object.
        // This example grants a normal DFU red rose. Real mods grant their own item.
        var gm = GameManager.Instance;
        if (gm == null || gm.PlayerEntity == null) return;
        gm.PlayerEntity.Items.AddItem(ItemBuilder.CreateItem(
            ItemGroups.PlantIngredients1, (int)PlantIngredients1.Red_rose));
    }

    void OnDestroy()
    {
        if (world != null) world.Dispose();
    }
}
