using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Mirror;
using UnityEngine;

// One handshake before Mirror creates a player. Shared by Steam, Direct IP and dedicated mode.
public sealed class ModCompatibilityAuthenticator : NetworkAuthenticator
{
    public struct Request : NetworkMessage
    {
        public int protocol;
        public string manifestJson;
    }
    public struct Response : NetworkMessage
    {
        public bool accepted;
        public string reason;
    }

    const int Protocol = 1;
    const float HandshakeTimeout = 12f;
    readonly HashSet<NetworkConnection> pending = new HashSet<NetworkConnection>();
    ModCompatibilityPolicy policy = new ModCompatibilityPolicy();
    string lastFailure = "";
    float failureUntil;

    public static ModCompatibilityAuthenticator Install(NetworkManager mgr)
    {
        if (mgr == null) throw new ArgumentNullException("mgr");
        var existing = mgr.authenticator;
        if (existing != null && !(existing is ModCompatibilityAuthenticator))
            throw new InvalidOperationException("A different Mirror authenticator is assigned; cannot replace it silently.");
        var auth = mgr.GetComponent<ModCompatibilityAuthenticator>();
        if (auth == null) auth = mgr.gameObject.AddComponent<ModCompatibilityAuthenticator>();
        mgr.authenticator = auth;
        return auth;
    }

    public void UseServerPolicyDirectory(string dir)
    {
        if (NetworkServer.active) throw new InvalidOperationException("Cannot change policy after server starts.");
        policy = ModCompatibilityPolicy.LoadFromDirectory(dir);
        policy.Validate();
    }

    public void UseListenHostPolicy()
    {
        string path = Path.Combine(DaggerfallWorkshop.DaggerfallUnity.Settings.PersistentDataPath, "Multiplayer");
        policy = ModCompatibilityPolicy.LoadFromDirectory(path);
    }

    public override void OnStartServer()
    {
        NetworkServer.RegisterHandler<Request>(HandleRequest, false);
    }
    public override void OnStopServer()
    {
        NetworkServer.UnregisterHandler<Request>();
        pending.Clear();
    }
    public override void OnServerAuthenticate(NetworkConnection conn)
    {
        pending.Add(conn);
        StartCoroutine(ServerTimeout(conn));
    }
    IEnumerator ServerTimeout(NetworkConnection conn)
    {
        yield return new WaitForSecondsRealtime(HandshakeTimeout);
        if (!pending.Remove(conn)) yield break;
        if (conn != null && !conn.isAuthenticated)
        {
            conn.Send(new Response { accepted = false, reason = "Mod handshake timed out. Use a compatible version of DFU Co-op." });
            yield return new WaitForSecondsRealtime(0.8f);
            if (conn != null && !conn.isAuthenticated) ServerReject(conn);
        }
    }
    void HandleRequest(NetworkConnection conn, Request request)
    {
        if (!pending.Remove(conn)) return; // ignores duplicates and unsolicited auth requests
        string reason;
        if (request.protocol != Protocol) reason = "Incompatible DFU Co-op mod-handshake protocol.";
        else
        {
            try
            {
                if (string.IsNullOrEmpty(request.manifestJson) || request.manifestJson.Length > 262144)
                    reason = "Invalid or oversized client mod manifest.";
                else
                {
                    var manifest = JsonUtility.FromJson<ModCompatibilityPolicy.Manifest>(request.manifestJson);
                    reason = policy.Check(ModCompatibilityPolicy.InstalledEnabledMods(), manifest == null ? null : manifest.mods);
                }
            }
            catch (Exception ex) { reason = "Invalid mod manifest: " + ex.Message; Debug.LogWarning("[ModCompatibility] " + ex); }
        }
        if (reason == null)
        {
            conn.Send(new Response { accepted = true, reason = "" });
            ServerAccept(conn);
        }
        else
        {
            Debug.LogWarning("[ModCompatibility] Rejected client " + conn.connectionId + ": " + reason);
            conn.Send(new Response { accepted = false, reason = reason });
            StartCoroutine(RejectSoon(conn));
        }
    }
    IEnumerator RejectSoon(NetworkConnection conn)
    {
        yield return new WaitForSecondsRealtime(0.8f);
        if (conn != null && !conn.isAuthenticated) ServerReject(conn);
    }

    public override void OnStartClient()
    {
        lastFailure = "";
        NetworkClient.RegisterHandler<Response>((Action<Response>)HandleResponse, false);
    }
    public override void OnStopClient()
    {
        NetworkClient.UnregisterHandler<Response>();
    }
    public override void OnClientAuthenticate()
    {
        var manifest = new ModCompatibilityPolicy.Manifest { mods = ModCompatibilityPolicy.InstalledEnabledMods() };
        NetworkClient.connection.Send(new Request { protocol = Protocol, manifestJson = JsonUtility.ToJson(manifest) });
        StartCoroutine(ClientTimeout());
    }
    IEnumerator ClientTimeout()
    {
        yield return new WaitForSecondsRealtime(HandshakeTimeout + 1f);
        if (NetworkClient.active && NetworkClient.connection != null && !NetworkClient.connection.isAuthenticated)
            RejectLocally("Server did not respond to the mod compatibility handshake. Update both host and client to the same DFU Co-op build.");
    }
    void HandleResponse(Response response)
    {
        if (response.accepted) ClientAccept();
        else RejectLocally(string.IsNullOrEmpty(response.reason) ? "Server rejected the mod list." : response.reason);
    }
    void RejectLocally(string reason)
    {
        lastFailure = reason.Length > 3000 ? reason.Substring(0, 3000) : reason;
        failureUntil = Time.unscaledTime + 30f;
        Debug.LogError("[ModCompatibility] " + lastFailure);
        if (NetworkClient.connection != null) ClientReject();
    }
    void OnGUI()
    {
        if (string.IsNullOrEmpty(lastFailure) || Time.unscaledTime > failureUntil || NetworkServer.active) return;
        float width = Mathf.Min(Screen.width - 24, 670f);
        float height = Mathf.Min(Screen.height - 24, 360f);
        GUILayout.BeginArea(new Rect((Screen.width-width)/2f, (Screen.height-height)/2f, width, height), GUI.skin.box);
        GUILayout.Label("Unable to join server — Mod compatibility");
        GUILayout.TextArea(lastFailure, GUILayout.ExpandHeight(true));
        if (GUILayout.Button("Close", GUILayout.Height(32))) lastFailure = "";
        GUILayout.EndArea();
    }
}
