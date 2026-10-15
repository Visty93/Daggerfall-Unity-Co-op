// Temporary diagnostic. Does not change transforms, collision, health or authority.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

public sealed class DedicatedDungeonFloorProbe : MonoBehaviour
{
    sealed class Sample
    {
        public SetupDemoEnemy enemy;
        public Vector3 initial;
        public Vector3 previous;
        public string owner;
        public float nextCheck;
        public float expires;
        public int drops;
        public bool settled;
    }

    readonly Dictionary<int, Sample> samples = new Dictionary<int, Sample>();
    StreamWriter output;
    float nextDiscovery;
    float nextFlush;
    int lines;
    bool failed;
    string runPath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindObjectOfType<DedicatedDungeonFloorProbe>() != null) return;
        var root = new GameObject("Temporary dungeon floor diagnostic");
        DontDestroyOnLoad(root);
        root.AddComponent<DedicatedDungeonFloorProbe>();
    }

    static string V(Vector3 value)
    {
        return string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", value.x, value.y, value.z);
    }

    string Role()
    {
        return NetworkServer.active ? (NetworkClient.active ? "listen-host" : "dedicated") : "client";
    }

    string Ownership(NetworkIdentity id)
    {
        if (id == null) return "no-identity";
        if (NetworkServer.active)
            return id.connectionToClient == null ? "server" : "connection:" + id.connectionToClient.connectionId;
        return id.hasAuthority ? "local-owner" : "observer";
    }

    void Write(string value)
    {
        if (failed || lines >= 3000) return;
        try
        {
            if (output == null)
            {
                string folder = Path.Combine(Application.persistentDataPath, "DungeonDiagnostics");
                Directory.CreateDirectory(folder);
                runPath = Path.Combine(folder, "DungeonFloor_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_PID" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
                output = new StreamWriter(runPath, false);
                Debug.Log("[DungeonFloorProbe] Separate diagnostic log: " + runPath);
            }
            output.WriteLine(DateTime.UtcNow.ToString("O") + " role=" + Role() + " frame=" + Time.frameCount + " realtime=" + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture) + " " + value);
            lines++;
        }
        catch (Exception error)
        {
            failed = true;
            Debug.LogWarning("[DungeonFloorProbe] Diagnostic file unavailable: " + error.Message);
            if (output != null) { output.Dispose(); output = null; }
        }
    }

    void LateUpdate()
    {
        if ((!NetworkServer.active && !NetworkClient.active) || failed || lines >= 3000) return;
        float now = Time.realtimeSinceStartup;
        if (now >= nextDiscovery)
        {
            nextDiscovery = now + 0.5f;
            foreach (SetupDemoEnemy enemy in FindObjectsOfType<SetupDemoEnemy>())
            {
                if (!enemy.isDungeonEnemy || samples.ContainsKey(enemy.GetInstanceID())) continue;
                var sample = new Sample { enemy = enemy, initial = enemy.transform.position, previous = enemy.transform.position, expires = now + 120f };
                samples.Add(enemy.GetInstanceID(), sample);
            }
        }
        foreach (Sample sample in samples.Values)
        {
            if (sample.enemy == null || now > sample.expires || now < sample.nextCheck) continue;
            sample.nextCheck = now + 0.1f;
            var id = sample.enemy.GetComponent<NetworkIdentity>();
            if (id == null || id.netId == 0) continue;
            string owner = Ownership(id);
            Vector3 pos = sample.enemy.transform.position;
            string reason = null;
            if (sample.owner == null) reason = "first-observed";
            else if (sample.owner != owner) reason = "ownership-change";
            else if (!sample.settled && now >= sample.expires - 118f) { sample.settled = true; reason = "after-two-seconds"; }
            else if (sample.drops < 6 && pos.y < sample.previous.y - 1f) { sample.drops++; reason = "downward-step"; }
            if (reason != null) Capture(sample, id, reason, owner);
            sample.owner = owner;
            sample.previous = pos;
        }
        if (output != null && now >= nextFlush)
        {
            nextFlush = now + 0.5f;
            try { output.Flush(); } catch (Exception) { failed = true; }
        }
    }

    void Capture(Sample sample, NetworkIdentity id, string reason, string owner)
    {
        Transform t = sample.enemy.transform;
        var cc = sample.enemy.GetComponent<CharacterController>();
        var motor = sample.enemy.GetComponent<EnemyMotor>();
        var rb = sample.enemy.GetComponent<Rigidbody>();
        float feetY = cc != null ? t.TransformPoint(cc.center).y - cc.height * Mathf.Abs(t.lossyScale.y) * 0.5f : t.position.y;
        Vector3 origin = new Vector3(t.position.x, feetY + 1f, t.position.z);
        int mask = 0;
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(t.gameObject.layer, layer)) mask |= 1 << layer;
        RaycastHit nearest = new RaycastHit();
        bool found = false;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, 100f, mask, QueryTriggerInteraction.Ignore))
        {
            if (!IsGeometry(hit.collider)) continue;
            if (!found || hit.distance < nearest.distance) { nearest = hit; found = true; }
        }
        DaggerfallDungeon dungeon = null;
        float distance = float.MaxValue;
        foreach (DaggerfallDungeon candidate in FindObjectsOfType<DaggerfallDungeon>())
        {
            float d = Mathf.Abs(candidate.transform.position.y - sample.initial.y);
            if (d < distance) { distance = d; dungeon = candidate; }
        }
        int active = 0, meshCount = 0, usableMeshes = 0;
        bool directFound = false;
        RaycastHit direct = new RaycastHit();
        string directName = "none";
        if (dungeon != null)
        {
            foreach (Collider collider in dungeon.GetComponentsInChildren<Collider>(true))
            {
                if (!IsGeometry(collider) || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger) continue;
                active++;
                var mesh = collider as MeshCollider;
                if (mesh != null)
                {
                    meshCount++;
                    if (mesh.sharedMesh != null && mesh.sharedMesh.vertexCount > 0) usableMeshes++;
                }
                RaycastHit hit;
                if (collider.Raycast(new Ray(origin, Vector3.down), out hit, 100f) && (!directFound || hit.distance < direct.distance))
                { directFound = true; direct = hit; directName = collider.name; }
            }
        }
        Write("event=" + reason + " netId=" + id.netId + " enemy='" + t.name + "' owner=" + owner +
            " pos=" + V(t.position) + " initial=" + V(sample.initial) + " previous=" + V(sample.previous) +
            " local=" + V(t.localPosition) + " parent='" + (t.parent != null ? t.parent.name : "root") + "'" +
            " cc=" + (cc != null ? "enabled:" + cc.enabled + ",grounded:" + cc.isGrounded + ",height:" + cc.height.ToString(CultureInfo.InvariantCulture) + ",center:" + V(cc.center) + ",velocity:" + V(cc.velocity) : "none") +
            " motor=" + (motor != null ? "enabled:" + motor.enabled + ",falls:" + motor.Falls + ",lastGroundY:" + motor.LastGroundedY.ToString(CultureInfo.InvariantCulture) : "none") +
            " rb=" + (rb != null ? "kinematic:" + rb.isKinematic + ",gravity:" + rb.useGravity : "none") +
            " rayOrigin=" + V(origin) + " sceneFloor=" + (found ? nearest.collider.name + "@" + V(nearest.point) : "none-within-100m") +
            " directFloor=" + (directFound ? directName + "@" + V(direct.point) : "none-within-100m") +
            " dungeon='" + (dungeon != null ? dungeon.name + "@" + V(dungeon.transform.position) : "none") + "'" +
            " activeGeometry=" + active + " meshes=" + meshCount + " usableMeshes=" + usableMeshes +
            " fullSize=" + !DaggerfallUnity.Settings.SmallerDungeons + " disableAI=" + GameManager.Instance.DisableAI + " classicTick=" + GameManager.ClassicUpdate + " timeScale=" + Time.timeScale.ToString(CultureInfo.InvariantCulture));
    }

    static bool IsGeometry(Collider collider)
    {
        return collider != null && !(collider is CharacterController) &&
            collider.GetComponentInParent<SetupDemoEnemy>() == null &&
            collider.GetComponentInParent<DaggerfallDungeon>() != null;
    }

    void OnApplicationQuit() { Close(); }
    void OnDestroy() { Close(); }
    void Close()
    {
        if (output == null) return;
        try { output.Dispose(); } catch (Exception) { }
        output = null;
    }
}
