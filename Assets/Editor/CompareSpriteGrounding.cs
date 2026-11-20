// Editor-only diagnostic for DFU Co-op remote-player and enemy mobile sprites.
// Place this file in Assets/Editor/CompareSpriteGrounding.cs.
// Does not change positions, collisions, gameplay state or network data.
#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop;

public static class CompareSpriteGrounding
{
    private const string MenuPath = "Tools/DFU Co-op/Compare Selected Sprite Grounding";

    [MenuItem(MenuPath, true)]
    private static bool ValidateCompare()
    {
        return EditorApplication.isPlaying && Selection.gameObjects.Length == 2;
    }

    [MenuItem(MenuPath)]
    private static void Compare()
    {
        GameObject[] selected = Selection.gameObjects;
        if (!EditorApplication.isPlaying || selected.Length != 2)
        {
            EditorUtility.DisplayDialog("Sprite grounding", "Enter Play Mode and select exactly TWO characters in the Hierarchy: a remote co-op player and a normal enemy.", "OK");
            return;
        }

        // Refresh physics so RaycastAll uses the latest character and terrain positions.
        Physics.SyncTransforms();
        var sb = new StringBuilder();
        sb.AppendLine("[SpriteGrounding] === Two-object comparison (Play Mode; read-only) ===");
        sb.AppendLine("Mesh bottom = bottom of the mobile's rendered quad, including any transparent pixels.");
        sb.AppendLine("Ground delta = mesh bottom Y - detected floor Y. Negative means quad below floor.");
        sb.AppendLine("Compare like-for-like class, gender and idle animation on level ground.");

        int valid = 0;
        foreach (GameObject obj in selected)
        {
            if (obj == null) continue;
            GameObject root = FindActorRoot(obj);
            DaggerfallMobileUnit mobile = root.GetComponentInChildren<DaggerfallMobileUnit>(true);
            MeshRenderer mesh = mobile != null ? mobile.GetComponent<MeshRenderer>() : null;
            CharacterController controller = root.GetComponent<CharacterController>();
            if (controller == null)
                controller = root.GetComponentInChildren<CharacterController>(true);

            sb.AppendLine();
            sb.AppendLine("Object selected: " + HierarchyPath(obj.transform));
            sb.AppendLine("Resolved actor root: " + HierarchyPath(root.transform));

            if (mobile == null || mesh == null)
            {
                sb.AppendLine("NO DaggerfallMobileUnit + MeshRenderer found; select the animated-sprite actor, not a paperdoll or non-mobile prefab.");
                continue;
            }

            ++valid;
            Bounds bounds = mesh.bounds;
            sb.AppendLine("Actor root world position: " + root.transform.position.ToString("F4"));
            sb.AppendLine("Mobile local position: " + mobile.transform.localPosition.ToString("F4"));
            sb.AppendLine("Mobile world position: " + mobile.transform.position.ToString("F4"));
            sb.AppendLine("Mobile world scale: " + mobile.transform.lossyScale.ToString("F4"));
            sb.AppendLine("Rendered mesh height: " + bounds.size.y.ToString("F4"));
            sb.AppendLine("Rendered mesh lower edge Y: " + bounds.min.y.ToString("F4"));
            sb.AppendLine("Rendered mesh top edge Y: " + bounds.max.y.ToString("F4"));
            if (controller != null)
            {
                sb.AppendLine("CharacterController height: " + controller.height.ToString("F4") +
                    " center: " + controller.center.ToString("F4"));
                sb.AppendLine("CharacterController lower edge Y: " + controller.bounds.min.y.ToString("F4"));
            }
            else
            {
                sb.AppendLine("No CharacterController found under resolved root.");
            }

            float groundY;
            string groundName;
            if (TryFindGround(bounds.center, Mathf.Max(bounds.max.y, root.transform.position.y) + 4f,
                out groundY, out groundName))
            {
                sb.AppendLine("Ground surface: " + groundName + " at Y=" + groundY.ToString("F4"));
                sb.AppendLine("MESH-BOTTOM / GROUND DELTA: " + (bounds.min.y - groundY).ToString("+0.0000;-0.0000;0.0000") + " units");
                if (controller != null)
                    sb.AppendLine("Controller-bottom / ground delta: " +
                        (controller.bounds.min.y - groundY).ToString("+0.0000;-0.0000;0.0000") + " units");
            }
            else
            {
                sb.AppendLine("No non-character, non-trigger floor collider detected below mesh center within 60 units.");
                sb.AppendLine("Floor may lack a physics collider; mesh and controller bounds above are still useful.");
            }
        }

        if (valid != 2)
            sb.AppendLine("[SpriteGrounding] A full comparison needs two actors that contain an animated DaggerfallMobileUnit.");
        sb.AppendLine("[SpriteGrounding] This script did not move or modify either object.");
        Debug.Log(sb.ToString());
        EditorUtility.DisplayDialog("Sprite grounding", "Comparison logged to the Unity Console. Search for [SpriteGrounding] and expand the log entry.", "OK");
    }

    private static GameObject FindActorRoot(GameObject selection)
    {
        // PlayerMultiplayer exists at the remote player's root; SetupDemoEnemy at a normal enemy root.
        // Check by component name so this diagnostic does not require those assemblies at compile time.
        for (Transform t = selection.transform; t != null; t = t.parent)
        {
            MonoBehaviour[] scripts = t.GetComponents<MonoBehaviour>();
            foreach (MonoBehaviour script in scripts)
            {
                if (script == null) continue;
                string typeName = script.GetType().Name;
                if (typeName == "PlayerMultiplayer" || typeName == "SetupDemoEnemy")
                    return t.gameObject;
            }
        }
        return selection;
    }

    private static string HierarchyPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }

    private static bool TryFindGround(Vector3 meshCenter, float originY, out float groundY, out string groundName)
    {
        groundY = 0f;
        groundName = null;
        Ray ray = new Ray(new Vector3(meshCenter.x, originY, meshCenter.z), Vector3.down);
        RaycastHit[] hits = Physics.RaycastAll(ray, 60f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        bool found = false;
        float bestY = float.NegativeInfinity;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null) continue;
            // Exclude colliders on characters (both the measured actor and any overlapping actors).
            if (hit.collider.GetComponentInParent<CharacterController>() != null) continue;
            // Exclude ordinary objects above the measured sprite's lower edge by requiring a plausible floor below the midpoint.
            if (hit.point.y > meshCenter.y + 0.3f) continue;
            if (hit.point.y > bestY)
            {
                bestY = hit.point.y;
                groundName = HierarchyPath(hit.collider.transform);
                found = true;
            }
        }
        groundY = found ? bestY : 0f;
        return found;
    }
}
#endif
