// Multiplayer adapter only. All lighting calculations run inside the installed FPL mod.
// This file contains no copied FPL lighting implementation.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility.ModSupport;

/// <summary>
/// Runs the installed FPL calculation using Unity's default raycast mask. The
/// existing local MP body must already use Ignore Raycast. This adapter reads
/// physics only and writes lighting/tint results; it never changes a collider,
/// layer, controller, transform, collision setting, or physics synchronization.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class FirstPersonLightingMultiplayerCompat : MonoBehaviour
{
    const string Prefix = "[MPFPLCompat v5] ";
    const int IgnoreRaycastLayer = 2;
    static FirstPersonLightingMultiplayerCompat instance;

    Mod lightingMod;
    MonoBehaviour lightingBehaviour;
    MethodInfo measureLight;
    Type bindingType;
    FieldInfo instanceField, cacheField, lightsField;
    IDictionary<DaggerfallEntityBehaviour, Color> visibilityCache;
    Transform previousLocalRoot;
    DaggerfallEntityBehaviour previousSubject;
    float nextBindAttempt;
    bool warnedBinding;
    bool warnedLayer;
    bool darkTorchLastSample;
    float nextDarkTorchLog;
    bool loggedCorrection;
    bool retryPending;
    float nextRetry, nextErrorLog;
    int failedAttempts;
    bool hasContext, previousInside, previousDungeon, logContext;
    bool lightSnapshotPending;
    GameObject previousArea;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance != null) return;
        var root = new GameObject("MP First-Person Lighting Compatibility");
        DontDestroyOnLoad(root);
        root.AddComponent<FirstPersonLightingMultiplayerCompat>();
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this);
            return;
        }
        instance = this;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    void LateUpdate()
    {
        if (!NetworkClient.active || NetworkClient.localPlayer == null)
        {
            previousLocalRoot = null;
            previousSubject = null;
            ClearBinding();
            nextBindAttempt = nextRetry = nextErrorLog = 0;
            failedAttempts = 0;
            retryPending = false;
            hasContext = false;
            previousArea = null;
            warnedLayer = false;
            darkTorchLastSample = false;
            loggedCorrection = false;
            return;
        }
        if (GameManager.IsGamePaused || !GameManager.HasInstance) return;
        try
        {
            var game = GameManager.Instance;
            var saves = SaveLoadManager.Instance;
            if (!game.IsReady || (saves != null && saves.LoadInProgress)) return;
            var enter = game.PlayerEnterExit;
            if (enter == null || enter.IsRespawning) return;
            var subject = game.PlayerEntityBehaviour;
            Transform localRoot = NetworkClient.localPlayer.transform;
            if (subject == null || subject.Entity == null || localRoot == subject.transform ||
                game.PlayerObject == null || !game.PlayerObject.activeInHierarchy) return;
            if (enter.IsPlayerInsideDungeon && (enter.Dungeon == null || !enter.Dungeon.isSet)) return;

            // Match FPL's existing refresh cadence, after its Update and before OnGUI.
            bool changedPlayer = previousLocalRoot != localRoot || previousSubject != subject;
            if (!changedPlayer && Time.frameCount % 5 != 0) return;
            GameObject area = enter.IsPlayerInsideDungeon ? enter.Dungeon.gameObject :
                (enter.IsPlayerInside && enter.Interior != null ? enter.Interior.gameObject : null);
            if (changedPlayer || !hasContext || previousInside != enter.IsPlayerInside ||
                previousDungeon != enter.IsPlayerInsideDungeon || previousArea != area)
            {
                hasContext = true;
                previousInside = enter.IsPlayerInside;
                previousDungeon = enter.IsPlayerInsideDungeon;
                previousArea = area;
                previousLocalRoot = localRoot;
                previousSubject = subject;
                ClearBinding();
                nextBindAttempt = nextRetry = 0;
                warnedLayer = darkTorchLastSample = false;
                nextDarkTorchLog = Time.unscaledTime + 0.5f;
                logContext = true;
                lightSnapshotPending = true;
            }
            if (Time.unscaledTime < nextRetry) return;
            if (!EnsureBinding()) return;
            if (localRoot.gameObject.layer != IgnoreRaycastLayer)
            {
                if (!warnedLayer)
                {
                    warnedLayer = true;
                    Debug.LogWarning(Prefix + "Local MP root is on layer " + localRoot.gameObject.layer +
                        ", not Ignore Raycast. Correction is inactive; no layers were changed. " +
                        "Check the existing PlayerMultiplayerCollisionProxy local-raycast setting.");
                }
                return;
            }

            Color previousLighting;
            bool hadLighting = visibilityCache.TryGetValue(subject, out previousLighting);
            // Only the mask passed to this calculation changes. There are no
            // collider toggles, layer changes, transform writes, or physics syncs.
            Color corrected = (Color)measureLight.Invoke(lightingBehaviour, new object[]
            {
                subject.transform.position, subject.gameObject, Physics.DefaultRaycastLayers
            });

            visibilityCache[subject] = corrected;
            Color tint;
            if (!TryReadPlayerTint(out tint))
                throw new InvalidOperationException("FPL did not return a playerTint colour.");
            if (game.RightHandWeapon != null) game.RightHandWeapon.Tint = tint;
            if (game.TransportManager != null) game.TransportManager.Tint = tint;

            if (retryPending)
            {
                Debug.Log(Prefix + "Resumed tint correction after " + failedAttempts + " failed attempt(s).");
                retryPending = false;
                failedAttempts = 0;
                nextErrorLog = 0;
            }
            if (logContext)
            {
                Debug.Log(Prefix + "Tint correction active. runtime=" + (Application.isEditor ? "editor" : "build") +
                    " area=" + (area != null ? area.name + "#" + area.GetInstanceID() : "exterior") +
                    " dungeon=" + enter.IsPlayerInsideDungeon + " MPBodyLayer=" + localRoot.gameObject.layer +
                    " FPL=" + lightingBehaviour.GetInstanceID() + " tint=" + tint);
                logContext = false;
            }
            TraceDarkTorch(game, tint);

            if (!loggedCorrection && hadLighting && previousLighting != corrected)
            {
                loggedCorrection = true;
                Debug.Log(Prefix + "Corrected local tint using FPL's own calculation and Unity's default query mask. " +
                    "No collider, layer, transform, or physics state was changed.");
            }
        }
        catch (Exception ex)
        {
            // Transitions can briefly invalidate scene references. Retry against
            // the current mod instance instead of disabling the adapter forever.
            failedAttempts++;
            retryPending = true;
            nextRetry = Time.unscaledTime + 1f;
            ClearBinding();
            nextBindAttempt = nextRetry;
            if (Time.unscaledTime >= nextErrorLog)
            {
                nextErrorLog = Time.unscaledTime + 15f;
                var detail = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Debug.LogWarning(Prefix + "Tint correction interrupted; will retry automatically. " + detail);
            }
        }
    }

    void TraceDarkTorch(GameManager game, Color tint)
    {
        // Diagnostics only; these are raw hits, not FPL's list of blockers.
        var source = game.PlayerObject != null ? game.PlayerObject.GetComponent<EnablePlayerTorch>() : null;
        GameObject torch = source != null ? source.PlayerTorch : null;
        Light light = torch != null ? torch.GetComponent<Light>() : null;
        if (game.PlayerEntity.LightSource == null)
        {
            darkTorchLastSample = false;
            return;
        }
        bool darkWithTorch = tint.maxColorComponent < 0.25f;
        if (!darkWithTorch) darkTorchLastSample = false;
        if ((!darkWithTorch || darkTorchLastSample) && !lightSnapshotPending) return;
        if (Time.unscaledTime < nextDarkTorchLog) return;
        lightSnapshotPending = false;
        darkTorchLastSample = darkWithTorch;
        nextDarkTorchLog = Time.unscaledTime + 2f;

        Vector3 location = game.PlayerEntityBehaviour.transform.position;
        Vector3 origin = light != null ? light.transform.position : location;
        float length = Vector3.Distance(origin, location);
        var descriptions = new List<string>();
        if (length > 0.00001f)
        {
            var hits = Physics.RaycastAll(new Ray(origin, location - origin), length,
                Physics.DefaultRaycastLayers);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length && descriptions.Count < 8; i++)
            {
                if (hits[i].collider == null) continue;
                var obstacle = hits[i].collider.gameObject;
                descriptions.Add(obstacle.name + "@" + hits[i].distance.ToString("F3") +
                    " layer=" + obstacle.layer + " trigger=" + hits[i].collider.isTrigger);
            }
        }
        var gathered = lightsField != null
            ? lightsField.GetValue(lightingBehaviour) as ICollection<Light> : null;
        string message = Prefix + (darkWithTorch ? "Dark tint while holding a light item." : "Carried-light sample.") +
            " tint=" + tint +
            " lightExists=" + (light != null) + " active=" + (light != null && light.isActiveAndEnabled) +
            " light=" + origin + " sample=" + location + " distance=" + length.ToString("F3") +
            " intensity=" + (light != null ? light.intensity.ToString("F3") : "missing") +
            " range=" + (light != null ? light.range.ToString("F3") : "missing") +
            " gathered=" + (gathered != null ? gathered.Count.ToString() : "unknown") +
            " torchGathered=" + (gathered != null && light != null && gathered.Contains(light)) +
            " area=" + (previousArea != null ? previousArea.name + "#" + previousArea.GetInstanceID() : "exterior") +
            " MPBodyLayer=" + NetworkClient.localPlayer.gameObject.layer +
            " concealed=" + game.PlayerEntity.IsMagicallyConcealed +
            " rawRayHits=[" + string.Join("; ", descriptions.ToArray()) + "]";
        if (darkWithTorch) Debug.LogWarning(message);
        else Debug.Log(message);
    }

    bool EnsureBinding()
    {
        if (Time.unscaledTime < nextBindAttempt) return false;
        if (lightingMod == null || !lightingMod.Enabled || !lightingMod.IsReady || lightingMod.MessageReceiver == null)
        {
            ClearBinding();
            nextBindAttempt = Time.unscaledTime + 2f;
            if (ModManager.Instance == null) return false;
            lightingMod = ModManager.Instance.GetMod("First-Person-Lighting");
            if (lightingMod == null || !lightingMod.Enabled || !lightingMod.IsReady ||
                lightingMod.MessageReceiver == null) return false;
        }
        Type type = lightingMod.MessageReceiver.Method.DeclaringType;
        if (type == null) return false;
        if (bindingType != type || instanceField == null || cacheField == null || measureLight == null)
        {
            bindingType = type;
            instanceField = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            measureLight = type.GetMethod("MeasureLight", flags, null,
                new Type[] { typeof(Vector3), typeof(GameObject), typeof(int) }, null);
            cacheField = type.GetField("visibilityCache", flags);
            lightsField = type.GetField("lights", flags); // Optional diagnostics only.
        }
        // playerTint uses the mod's current static Instance. Write that same
        // instance's current cache, even if an older behaviour is still alive.
        var behaviour = instanceField != null ? instanceField.GetValue(null) as MonoBehaviour : null;
        if (behaviour == null || !behaviour.isActiveAndEnabled)
        {
            nextBindAttempt = Time.unscaledTime + 2f;
            return false;
        }
        visibilityCache = cacheField != null
            ? cacheField.GetValue(behaviour) as IDictionary<DaggerfallEntityBehaviour, Color> : null;
        if (measureLight == null || measureLight.ReturnType != typeof(Color) ||
            visibilityCache == null)
        {
            nextBindAttempt = Time.unscaledTime + 2f;
            if (!warnedBinding)
            {
                warnedBinding = true;
                Debug.LogWarning(Prefix + "This FPL version has different method/cache bindings; correction is inactive.");
            }
            return false;
        }
        lightingBehaviour = behaviour;
        nextBindAttempt = 0;
        return true;
    }

    void ClearBinding()
    {
        lightingMod = null;
        lightingBehaviour = null;
        visibilityCache = null;
        bindingType = null;
        instanceField = cacheField = lightsField = null;
        measureLight = null;
    }

    bool TryReadPlayerTint(out Color color)
    {
        Color replyColor = Color.clear;
        bool received = false;
        // FPL applies its own concealment/alpha rules to the new lighting entry.
        lightingMod.MessageReceiver("playerTint", null, (string reply, object data) =>
        {
            if (data is Color)
            {
                replyColor = (Color)data;
                received = true;
            }
        });
        color = replyColor;
        return received;
    }
}
