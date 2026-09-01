using System.Collections;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;

/// <summary>
/// Combined root collision + visual-child stabilizer for PlayerMultiplayer.
/// 
/// Put this file in Assets as:
///     PlayerMultiplayerCollisionProxy.cs
/// 
/// The class name must stay PlayerMultiplayerCollisionProxy, because Unity requires
/// the MonoBehaviour class name to match the .cs file name.
/// 
/// Attach this to the PlayerMultiplayer prefab root.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMultiplayerCollisionProxy : MonoBehaviour
{
    [Header("Root PlayerMultiplayer collision")]
    public float refreshInterval = 0.5f;

    [Tooltip("Keep true. Enemies need the root PlayerMultiplayer CharacterController for target/height checks.")]
    public bool keepRootControllerEnabled = true;

    [Tooltip("Keep true if you want players to collide with remote PlayerMultiplayer proxies.")]
    public bool keepRemotePlayersSolid = true;

    [Tooltip("Only ignore PlayerAdvanced against its own local PlayerMultiplayer proxy. Remote proxies remain solid.")]
    public bool ignoreOnlyOwnLocalProxy = true;

    [Header("Visual child stabilizer")]
    [Tooltip("Optional. Leave empty to auto-find the Enemy/Thief visual child that has the extra child CharacterController.")]
    public Transform visualRoot;

    [Tooltip("Captures the visual child's correct local position after one frame.")]
    public bool captureInitialVisualLocalPosition = true;

    [Tooltip("Used after capture. If capture is disabled, set this manually in the inspector.")]
    public Vector3 lockedVisualLocalPosition = Vector3.zero;

    [Tooltip("Lock the visual child local position so it cannot drift/fly above the PlayerMultiplayer root.")]
    public bool lockVisualLocalPosition = true;

    [Tooltip("Usually keep false. Billboard/sprite orientation code may need to rotate this child.")]
    public bool lockVisualLocalRotation = false;

    public Vector3 lockedVisualLocalEulerAngles = Vector3.zero;

    [Tooltip("Disable CharacterController/Collider/Rigidbody only on the visual child, not on the PlayerMultiplayer root.")]
    public bool disableVisualChildPhysics = true;

    [Tooltip("Repeat child-physics cleanup in case another script re-enables the child controller.")]
    public float visualSanitizeInterval = 0.5f;

    [Tooltip("Disable old experimental MultiplayerBodyBlocker child if it exists.")]
    public bool disableOldBodyBlockerChild = true;

    [Tooltip("Optional debug log when visual child Y drift is corrected.")]
    public bool logVisualCorrections = false;

    CharacterController proxyController;
    CharacterController localPlayerController;
    NetworkIdentity networkIdentity;

    bool visualCaptured;
    float nextCollisionRefreshTime;
    float nextVisualSanitizeTime;

    void Awake()
    {
        proxyController = GetComponent<CharacterController>();
        networkIdentity = GetComponent<NetworkIdentity>();
    }

    IEnumerator Start()
    {
        // Wait one frame so PlayerMultiplayer.setupLocal()/enableAll() has time to enable/disable the visual child.
        yield return null;

        FindVisualRootIfNeeded();
        CaptureVisualLocalTransformIfNeeded();
        SanitizeVisualChildPhysics();
        DisableOldBodyBlockerIfNeeded();
        ApplyRootCollisionSetup();
        ApplyVisualLock(true);
    }

    void Update()
    {
        if (Time.time >= nextCollisionRefreshTime)
        {
            nextCollisionRefreshTime = Time.time + refreshInterval;
            ApplyRootCollisionSetup();
            DisableOldBodyBlockerIfNeeded();
        }

        if (disableVisualChildPhysics && Time.time >= nextVisualSanitizeTime)
        {
            nextVisualSanitizeTime = Time.time + visualSanitizeInterval;
            FindVisualRootIfNeeded();
            SanitizeVisualChildPhysics();
        }
    }

    void LateUpdate()
    {
        FindVisualRootIfNeeded();
        CaptureVisualLocalTransformIfNeeded();
        ApplyVisualLock(false);
    }

    void ApplyRootCollisionSetup()
    {
        if (proxyController == null)
            proxyController = GetComponent<CharacterController>();

        if (proxyController == null)
            return;

        // Do not leave this disabled. Enemies target/check height against this root controller.
        if (keepRootControllerEnabled && !proxyController.enabled)
            proxyController.enabled = true;

        // Restore player-vs-player collision on remote proxies.
        proxyController.detectCollisions = keepRemotePlayersSolid;

        if (localPlayerController == null &&
            GameManager.Instance != null &&
            GameManager.Instance.PlayerObject != null)
        {
            localPlayerController = GameManager.Instance.PlayerObject.GetComponent<CharacterController>();
        }

        if (localPlayerController == null || localPlayerController == proxyController)
            return;

        bool isOwnLocalProxy = networkIdentity != null && networkIdentity.isLocalPlayer;

        // Critical behaviour:
        // - Own PlayerAdvanced ignores own hidden/local PlayerMultiplayer proxy.
        // - Own PlayerAdvanced does NOT ignore remote PlayerMultiplayer proxies.
        // This keeps remote players physically blocking each other without colliding with yourself.
        if (ignoreOnlyOwnLocalProxy)
            Physics.IgnoreCollision(localPlayerController, proxyController, isOwnLocalProxy);
    }

    void FindVisualRootIfNeeded()
    {
        if (visualRoot != null)
            return;

        if (proxyController == null)
            proxyController = GetComponent<CharacterController>();

        // Best auto-detect:
        // find a child CharacterController that is not the root PlayerMultiplayer controller.
        CharacterController[] controllers = GetComponentsInChildren<CharacterController>(true);
        for (int i = 0; i < controllers.Length; i++)
        {
            CharacterController cc = controllers[i];
            if (cc == null)
                continue;

            if (cc != proxyController && cc.transform != transform)
            {
                visualRoot = cc.transform;
                return;
            }
        }

        // Fallback: first real child, excluding old helper objects.
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (child == null)
                continue;

            if (child.name == "MultiplayerBodyBlocker")
                continue;

            visualRoot = child;
            return;
        }
    }

    void CaptureVisualLocalTransformIfNeeded()
    {
        if (visualRoot == null || visualCaptured)
            return;

        if (captureInitialVisualLocalPosition)
            lockedVisualLocalPosition = visualRoot.localPosition;

        lockedVisualLocalEulerAngles = visualRoot.localEulerAngles;
        visualCaptured = true;
    }

    void SanitizeVisualChildPhysics()
    {
        if (!disableVisualChildPhysics || visualRoot == null)
            return;

        // This child is only the visible enemy/thief sprite representation.
        // It must not have working physics, otherwise its transform can be pushed upward
        // independently from the PlayerMultiplayer root.
        CharacterController[] childControllers = visualRoot.GetComponentsInChildren<CharacterController>(true);
        for (int i = 0; i < childControllers.Length; i++)
        {
            CharacterController cc = childControllers[i];
            if (cc == null)
                continue;

            // Do not ever disable the root PlayerMultiplayer controller here.
            if (cc == proxyController || cc.transform == transform)
                continue;

            cc.enabled = false;
        }

        Collider[] childColliders = visualRoot.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < childColliders.Length; i++)
        {
            Collider col = childColliders[i];
            if (col == null)
                continue;

            // Do not disable a root collider if something unusual returns it.
            if (col.transform == transform)
                continue;

            col.enabled = false;
        }

        Rigidbody[] childRigidbodies = visualRoot.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < childRigidbodies.Length; i++)
        {
            Rigidbody rb = childRigidbodies[i];
            if (rb == null)
                continue;

            // Do not touch root Rigidbody / NetworkRigidbody setup.
            if (rb.transform == transform)
                continue;

            rb.isKinematic = true;
            rb.useGravity = false;
            rb.detectCollisions = false;
        }
    }

    void ApplyVisualLock(bool force)
    {
        if (visualRoot == null || !visualCaptured)
            return;

        if (lockVisualLocalPosition)
        {
            Vector3 before = visualRoot.localPosition;

            if (force || (before - lockedVisualLocalPosition).sqrMagnitude > 0.0001f)
            {
                visualRoot.localPosition = lockedVisualLocalPosition;

                if (logVisualCorrections && Mathf.Abs(before.y - lockedVisualLocalPosition.y) > 0.05f)
                {
                    Debug.Log($"[PlayerMultiplayerCollisionProxy] Corrected visual child Y drift on '{name}/{visualRoot.name}' from localY={before.y:F3} to {lockedVisualLocalPosition.y:F3}");
                }
            }
        }

        if (lockVisualLocalRotation)
            visualRoot.localEulerAngles = lockedVisualLocalEulerAngles;
    }

    void DisableOldBodyBlockerIfNeeded()
    {
        if (!disableOldBodyBlockerChild)
            return;

        Transform oldBlocker = transform.Find("MultiplayerBodyBlocker");
        if (oldBlocker == null)
            return;

        Collider[] cols = oldBlocker.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] != null)
                cols[i].enabled = false;
        }

        Rigidbody[] rbs = oldBlocker.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rbs.Length; i++)
        {
            if (rbs[i] != null)
            {
                rbs[i].isKinematic = true;
                rbs[i].detectCollisions = false;
            }
        }

        oldBlocker.gameObject.SetActive(false);
    }

    [ContextMenu("Recapture Current Visual Local Position")]
    public void RecaptureCurrentVisualLocalPosition()
    {
        FindVisualRootIfNeeded();

        if (visualRoot == null)
            return;

        lockedVisualLocalPosition = visualRoot.localPosition;
        lockedVisualLocalEulerAngles = visualRoot.localEulerAngles;
        visualCaptured = true;
    }

    [ContextMenu("Force Reset Visual Now")]
    public void ForceResetVisualNow()
    {
        FindVisualRootIfNeeded();
        CaptureVisualLocalTransformIfNeeded();
        SanitizeVisualChildPhysics();
        ApplyVisualLock(true);
    }
}
