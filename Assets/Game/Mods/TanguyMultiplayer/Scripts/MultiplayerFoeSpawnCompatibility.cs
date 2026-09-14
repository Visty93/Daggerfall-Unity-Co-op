using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Questing;

namespace DaggerfallWorkshop.Utility
{
    /// <summary>
    /// Narrow client return-value compatibility for third-party spawn callers.
    /// Real enemy creation and networking remain in the existing multiplayer path.
    /// Static helper: does not need to be attached to a GameObject.
    /// </summary>
    public static class MultiplayerFoeSpawnCompatibility
    {
        public static GameObject[] GetClientReturnObjects(
            int count, Vector3 position, MobileTypes foeType,
            bool isDungeonFromClient, Foe foeResource)
        {
            if (!isDungeonFromClient || foeResource != null ||
                !IsHorribleHordesDungeonEntrySpawnCall())
                return null;

            GameObject[] returnObjects = CreateHorribleHordesClientReturnObjects(count, position);
            Debug.Log($"[MPHordeCompat] Client requested {count} x {foeType} at {position}; supplied temporary return objects so dungeon-entry processing can continue.");
            return returnObjects;
        }

        // Identify the callback by the names exposed in Unity's exception stack.
        // No mod methods are invoked, replaced, or loaded by this compatibility check.
        private static bool IsHorribleHordesDungeonEntrySpawnCall()
        {
            if (!NetworkClient.active || NetworkServer.active)
                return false;

            System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                System.Diagnostics.StackFrame frame = trace.GetFrame(i);
                System.Reflection.MethodBase method = frame != null ? frame.GetMethod() : null;
                if (method != null && method.DeclaringType != null &&
                    method.DeclaringType.FullName == "HorribleHordesMod.HorribleHordes" &&
                    method.Name == "CheckOnDungeonEntrance")
                    return true;
            }

            return false;
        }

        private static GameObject[] CreateHorribleHordesClientReturnObjects(int count, Vector3 position)
        {
            GameObject[] objects = new GameObject[count];
            for (int i = 0; i < count; i++)
            {
                GameObject temporary = new GameObject("MP Horde Spawn Return (temporary)");
                // These are array/activation handles only, never local or networked enemies.
                temporary.hideFlags = HideFlags.HideAndDontSave;
                temporary.transform.position = position;
                temporary.SetActive(false);
                objects[i] = temporary;

                // Unity defers destruction until after the current Update loop, allowing
                // the synchronous caller to use the entries before they are cleaned up.
                // Schedule immediately so cleanup also happens if the caller throws later.
                UnityEngine.Object.Destroy(temporary);
            }
            return objects;
        }

    }
}
