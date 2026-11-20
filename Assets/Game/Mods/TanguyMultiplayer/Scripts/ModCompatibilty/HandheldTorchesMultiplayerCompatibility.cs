// Runtime coordinate adapter and custom visual factory for installed Handheld Torches.
// The installed mod makes the water decision; no mod water formula is reproduced here.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallCoop.ObjectSync;

// Repair before GenericModObjectSync discovers the finished appearance at order 30000.
[DefaultExecutionOrder(29900)]
public sealed class HandheldTorchesMultiplayerCompatibility : MonoBehaviour
{
    const string Prefix = "[HandheldTorchesMP v2] ";
    const int Archive = 112358;
    const string FactoryId = "dfucoop.handheld-torches.billboard.v1";
    static HandheldTorchesMultiplayerCompatibility instance;
    Mod mod;
    MonoBehaviour controller;
    IList objects, durations, templates;
    MethodInfo spawnPlaced;
    Type projectileType;
    FieldInfo projectileTemplate, projectileDuration;
    readonly Dictionary<int, GameObject> observed = new Dictionary<int, GameObject>();
    readonly List<int> stale = new List<int>();
    float nextBind, nextProjectileScan, nextPrune;
    bool bindingWarning, repairLogged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("MP Handheld Torches Compatibility");
        DontDestroyOnLoad(go);
        go.AddComponent<HandheldTorchesMultiplayerCompatibility>();
    }
    void Awake()
    {
        if (instance != null && instance != this) { Destroy(this); return; }
        instance = this;
        if (!GenericModObjectSync.RegisterFactory(FactoryId, CapturePlacedAppearance, BuildPlacedAppearance))
            Debug.LogWarning(Prefix + "Placed-light factory was already registered.");
    }
    void OnDestroy() { if (instance == this) instance = null; }

    static bool SupportedRecord(int record)
    {
        return record == 0 || record == 1 || record == 2 || record == 10 || record == 11 || record == 12;
    }

    static string CapturePlacedAppearance(GameObject source)
    {
        var billboard = source != null ? source.GetComponent<Billboard>() : null;
        if (billboard == null || billboard.Summary.Archive != Archive || !SupportedRecord(billboard.Summary.Record))
            throw new InvalidOperationException("Unsupported Handheld Torches billboard.");
        // The generic native-billboard path caps archive numbers at 9999. Keep
        // this mod's custom archive local to its explicit reconstruction recipe.
        return billboard.Summary.Record.ToString(CultureInfo.InvariantCulture);
    }

    static GameObject BuildPlacedAppearance(ObjectSyncVisual description, GameObject existing)
    {
        int record;
        if (!int.TryParse(description.CustomData, NumberStyles.Integer, CultureInfo.InvariantCulture, out record) ||
            !SupportedRecord(record)) throw new InvalidOperationException("Invalid placed-light appearance record.");
        if (existing != null)
        {
            var current = existing.GetComponentInChildren<Billboard>(true);
            if (current == null) throw new InvalidOperationException("Missing placed-light billboard.");
            if (current.Summary.Archive != Archive || current.Summary.Record != record)
                current.SetMaterial(Archive, record);
            return existing;
        }
        var anchor = new GameObject("HandheldLightVisualAnchor");
        try
        {
            // The API moves the neutral anchor; the child keeps normal billboard
            // animation and faces each receiving player's own camera.
            var visual = GameObjectHelper.CreateDaggerfallBillboardGameObject(Archive, record, anchor.transform);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            return anchor;
        }
        catch { Destroy(anchor); throw; }
    }

    void LateUpdate()
    {
        if (!NetworkClient.active || NetworkClient.localPlayer == null)
        {
            observed.Clear(); repairLogged = false; return;
        }
        if (GameManager.IsGamePaused || !GameManager.HasInstance) return;
        var saves = SaveLoadManager.Instance;
        if (saves == null || saves.LoadInProgress) return;
        var game = GameManager.Instance;
        var enter = game.PlayerEnterExit;
        if (enter == null || !enter.IsPlayerInsideDungeon || enter.Dungeon == null || !enter.Dungeon.isSet) return;
        try
        {
            if (!Bind()) return;
            if (objects.Count != durations.Count || objects.Count != templates.Count) return;
            // Spawning a temporary author-created appearance appends to these lists.
            // Snapshot the pre-existing count; its temporary entry is removed in scope.
            int count = objects.Count;
            for (int i = 0; i < count; i++)
            {
                var go = objects[i] as GameObject;
                if (!FirstObservation(go)) continue;
                TryRebuildInDungeonFrame(go, Convert.ToInt32(templates[i]), Convert.ToSingle(durations[i]), false, enter);
            }
            if (projectileType != null && Time.unscaledTime >= nextProjectileScan)
            {
                nextProjectileScan = Time.unscaledTime + 0.05f;
                foreach (var item in UnityEngine.Object.FindObjectsOfType(projectileType))
                {
                    var projectile = item as Component;
                    if (projectile == null || !FirstObservation(projectile.gameObject)) continue;
                    TryRebuildInDungeonFrame(projectile.gameObject, Convert.ToInt32(projectileTemplate.GetValue(projectile)),
                        Convert.ToSingle(projectileDuration.GetValue(projectile)), true, enter);
                }
            }
            if (Time.unscaledTime >= nextPrune)
            {
                nextPrune = Time.unscaledTime + 5f;
                stale.Clear();
                foreach (var pair in observed) if (pair.Value == null) stale.Add(pair.Key);
                foreach (int id in stale) observed.Remove(id);
            }
        }
        catch (Exception error)
        {
            if (!bindingWarning)
            {
                bindingWarning = true;
                Debug.LogWarning(Prefix + "Could not inspect this mod version: " + error);
            }
        }
    }

    bool FirstObservation(GameObject go)
    {
        if (go == null || !go.activeInHierarchy) return false;
        GameObject known;
        int id = go.GetInstanceID();
        if (observed.TryGetValue(id, out known) && known == go) return false;
        observed[id] = go;
        return true;
    }

    void TryRebuildInDungeonFrame(GameObject go, int template, float remaining, bool projectile, PlayerEnterExit enter)
    {
        // The template and remaining duration come from the installed mod's own
        // registration, not a separately reproduced item/spawn implementation.
        if (template <= 0 || remaining <= 0f) return;
        var billboard = go.GetComponent<Billboard>();
        var renderer = go.GetComponent<Renderer>();
        if (billboard == null || renderer == null || billboard.Summary.Archive != Archive ||
            go.GetComponentInChildren<Light>(true) != null) return;

        // Convert the runtime placement point back to the dungeon's own frame.
        // The original installed spawn method then performs its original water
        // test unchanged, including any clearance/settings it owns internally.
        // A placed billboard's visible bottom gives a ground point without
        // reproducing the mod's texture-height offset formula.
        float bottom = renderer.bounds.min.y;
        Vector3 worldSample = go.transform.position;
        if (!projectile) worldSample.y = bottom;
        Vector3 dungeonSample = enter.Dungeon.transform.InverseTransformPoint(worldSample);

        GameObject prototype = null;
        Vector3 oldPosition = go.transform.position;
        int oldRecord = billboard.Summary.Record;
        int before = objects.Count;
        bool finished = false;
        try
        {
            // This temporary prop is created synchronously in the dungeon frame.
            // Its result is inspected/attached or removed before a frame elapses.
            // No player, dungeon or pre-existing collider is moved for this call.
            spawnPlaced.Invoke(controller, new object[] { template, dungeonSample, remaining });
            if (objects.Count != before + 1 || objects.Count != durations.Count || objects.Count != templates.Count)
                throw new InvalidOperationException("Unexpected placed-light list update.");
            prototype = objects[before] as GameObject;
            if (prototype == null) throw new InvalidOperationException("No author-created appearance.");
            var appearance = prototype.GetComponent<Billboard>();
            var hiddenRenderer = prototype.GetComponent<Renderer>();
            if (appearance == null || hiddenRenderer == null || appearance.Summary.Archive != Archive)
                throw new InvalidOperationException("Unsupported appearance from this mod version.");
            RemoveTemporaryRegistration(prototype);

            // If the original mod still produces an unlit result, keep its
            // decision. Genuine wet placement is not overridden by this helper.
            // The finally block removes this unused temporary prop.
            if (prototype.GetComponentInChildren<Light>(true) == null) return;
            if (!SupportedRecord(appearance.Summary.Record))
                throw new InvalidOperationException("Unsupported placed-light record from this mod version.");
            int litRecord = appearance.Summary.Record;

            // The temporary root supplies only the author's light and looping audio.
            // The existing prop retains its identity, pickup entry, fuel and projectile.
            appearance.enabled = false;
            hiddenRenderer.enabled = false;
            foreach (var collider in prototype.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            prototype.name = "MP Handheld Light Attachment";
            prototype.transform.SetParent(go.transform, false);
            prototype.transform.localPosition = Vector3.zero;
            prototype.transform.localRotation = Quaternion.identity;
            prototype.transform.localScale = Vector3.one;
            billboard.SetMaterial(Archive, litRecord);
            if (!projectile)
            {
                Vector3 placed = go.transform.position;
                placed.y += bottom - renderer.bounds.min.y;
                go.transform.position = placed;
            }
            finished = true;
            if (!repairLogged)
            {
                repairLogged = true;
                Debug.Log(Prefix + "Restored world light using the installed mod's decision in dungeon-local coordinates. " +
                    "Dungeon origin=" + enter.Dungeon.transform.position + " world sample=" + worldSample +
                    " dungeon sample=" + dungeonSample + ". Player/swimming settings unchanged.");
            }
        }
        catch (Exception error)
        {
            try { billboard.SetMaterial(Archive, oldRecord); go.transform.position = oldPosition; }
            catch { }
            Debug.LogWarning(Prefix + "Left original world-light state after repair failed: " + error);
        }
        finally
        {
            // If the author's call threw after appending, still clean its temporary entry.
            if (!finished && prototype == null && objects.Count > before)
                prototype = objects[before] as GameObject;
            if (prototype != null && !finished)
            {
                RemoveTemporaryRegistration(prototype);
                prototype.SetActive(false);
                Destroy(prototype);
            }
        }
    }

    void RemoveTemporaryRegistration(GameObject prototype)
    {
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i] as GameObject != prototype) continue;
            objects.RemoveAt(i);
            if (i < durations.Count) durations.RemoveAt(i);
            if (i < templates.Count) templates.RemoveAt(i);
            return;
        }
    }

    bool Bind()
    {
        if (controller != null && controller.isActiveAndEnabled && mod != null && mod.Enabled && mod.IsReady)
            return true;
        if (Time.unscaledTime < nextBind) return false;
        nextBind = Time.unscaledTime + 2f;
        if (ModManager.Instance == null) return false;
        mod = ModManager.Instance.GetMod("Handheld Torches");
        if (mod == null || !mod.Enabled || !mod.IsReady || mod.MessageReceiver == null) return false;
        Type type = mod.MessageReceiver.Method.DeclaringType;
        if (type == null) return false;
        var owner = mod.MessageReceiver.Target as MonoBehaviour;
        if (owner == null)
        {
            var singleton = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            owner = singleton != null ? singleton.GetValue(null) as MonoBehaviour : null;
        }
        if (owner == null) return false;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var objectField = type.GetField("billboardsObject", flags);
        var timeField = type.GetField("billboardsTime", flags);
        var templateField = type.GetField("billboardsItemTemplateIndex", flags);
        objects = objectField != null ? objectField.GetValue(owner) as IList : null;
        durations = timeField != null ? timeField.GetValue(owner) as IList : null;
        templates = templateField != null ? templateField.GetValue(owner) as IList : null;
        spawnPlaced = type.GetMethod("SpawnLightSource", flags, null,
            new Type[] { typeof(int), typeof(Vector3), typeof(float) }, null);
        if (objects == null || durations == null || templates == null || spawnPlaced == null)
        {
            if (!bindingWarning)
            {
                bindingWarning = true;
                Debug.LogWarning(Prefix + "Unsupported Handheld Torches member layout; adapter is inactive.");
            }
            return false;
        }
        projectileType = type.Assembly.GetType("HandheldTorchesProjectile", false);
        projectileTemplate = projectileType != null ? projectileType.GetField("templateIndex", flags) : null;
        projectileDuration = projectileType != null ? projectileType.GetField("time", flags) : null;
        if (projectileTemplate == null || projectileDuration == null) projectileType = null;
        controller = owner;
        return true;
    }
}
