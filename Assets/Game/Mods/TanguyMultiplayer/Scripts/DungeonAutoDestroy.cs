using System.Collections;
using UnityEngine;
using Mirror;

public class DungeonAutoDestroy : NetworkBehaviour
{
    public float checkInterval = 1f;
    public float destroyDistance = 300f;
    public float destroyDelay = 10f;

    private Coroutine destroyRoutine;

    void Start()
    {
        if (!NetworkServer.active)
        {
            enabled = false;
            return;
        }

        StartCoroutine(DistanceCheckRoutine());
    }

    IEnumerator DistanceCheckRoutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(checkInterval);

            if (!IsAnyPlayerInRange())
            {
                if (destroyRoutine == null)
                {
                    destroyRoutine = StartCoroutine(DestroyAfterDelay());
                }
            }
            else if (destroyRoutine != null)
            {
                StopCoroutine(destroyRoutine);
                destroyRoutine = null;
            }
        }
    }

    bool IsAnyPlayerInRange()
    {
        foreach (var player in FindObjectsOfType<PlayerMultiplayer>())
        {
            float distance = Vector3.Distance(player.transform.position, transform.position);
            if (distance < destroyDistance)
                return true;
        }
        return false;
    }

    IEnumerator DestroyAfterDelay()
    {
        Debug.Log($"[DungeonAutoDestroy] No players nearby. Destroying dungeon '{name}' in {destroyDelay} seconds.");
        yield return new WaitForSeconds(destroyDelay);
        Debug.Log($"[DungeonAutoDestroy] Destroying dungeon '{name}' (NetID: {netId})");
        NetworkServer.Destroy(gameObject);
    }
}
