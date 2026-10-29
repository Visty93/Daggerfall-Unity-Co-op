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

    // Reliable detail chunks precede the rejection response; keep strings below Mirror's limit.
    public struct FailureDetails : NetworkMessage { public string text; }
    const string ListenModeKey = "DFUCoop.ModMatching.Exact";
    bool listenHostPolicy;
    string receivedDetails = "";
    Vector2 failureScroll;
    GUIStyle failureStyle;

    public string ListenHostModeLabel { get { return policy.mode == "exact" ? "Exact" : policy.mode == "approved" ? "Approved" : "Relaxed"; } }
    public bool ListenHostExact { get { return policy.mode == "exact"; } }
    public void SetListenHostExact(bool exact)
    {
        if (!listenHostPolicy || DfuDedicatedServerOptions.Requested ||
            (NetworkClient.active && !NetworkServer.active)) return;
        policy.mode = exact ? "exact" : "relaxed";
        if (exact) policy.matchVersions = true;
        PlayerPrefs.SetInt(ListenModeKey, exact ? 1 : 0);
        PlayerPrefs.Save();
    }

    const int Protocol = 1;
    const float HandshakeTimeout = 12f;
    readonly HashSet<NetworkConnection> pending = new HashSet<NetworkConnection>();
    ModCompatibilityPolicy policy = new ModCompatibilityPolicy();
    string lastFailure = "";


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
        listenHostPolicy = false;
        policy = ModCompatibilityPolicy.LoadFromDirectory(dir);
        policy.Validate();
    }

    public void UseListenHostPolicy()
    {
        string path = Path.Combine(DaggerfallWorkshop.DaggerfallUnity.Settings.PersistentDataPath, "Multiplayer");
        policy = ModCompatibilityPolicy.LoadFromDirectory(path);
        listenHostPolicy = true;
        if (PlayerPrefs.HasKey(ListenModeKey))
            policy.mode = PlayerPrefs.GetInt(ListenModeKey, 0) != 0 ? "exact" : "relaxed";
        if (policy.mode == "exact") policy.matchVersions = true;
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
            for (int offset = 0; offset < reason.Length; offset += 2048)
                conn.Send(new FailureDetails { text = reason.Substring(offset, Math.Min(2048, reason.Length - offset)) });
            conn.Send(new Response { accepted = false, reason = "Mod compatibility failed. Update both peers if the detailed list is unavailable." });
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
        receivedDetails = "";
        NetworkClient.RegisterHandler<FailureDetails>((Action<FailureDetails>)(part =>
        {
            if (part.text != null && receivedDetails.Length + part.text.Length <= 2097152)
                receivedDetails += part.text;
        }), false);
        NetworkClient.RegisterHandler<Response>((Action<Response>)HandleResponse, false);
    }
    public override void OnStopClient()
    {
        NetworkClient.UnregisterHandler<Response>();
        NetworkClient.UnregisterHandler<FailureDetails>();
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
        else RejectLocally(!string.IsNullOrEmpty(receivedDetails) ? receivedDetails :
            (string.IsNullOrEmpty(response.reason) ? "Server rejected the mod list." : response.reason));
    }
    void RejectLocally(string reason)
    {
        lastFailure = reason;
        failureScroll = Vector2.zero;
        Debug.LogError("[ModCompatibility] " + lastFailure);
        if (NetworkClient.connection != null) ClientReject();
    }
    void OnGUI()
    {
        if (string.IsNullOrEmpty(lastFailure) || NetworkServer.active) return;
        // Escape belongs to the DFU pause menu; it must not also dismiss this report.
        Event currentEvent = Event.current;
        if (currentEvent.type == EventType.KeyDown &&
            (currentEvent.keyCode == KeyCode.Return ||
             currentEvent.keyCode == KeyCode.KeypadEnter))
        {
            lastFailure = "";
            failureScroll = Vector2.zero;
            currentEvent.Use();
            return;
        }
        // Independent of other HUD scaling, with fixed header/footer and a clipped scroll body.
        Matrix4x4 oldMatrix = GUI.matrix;
        int oldDepth = GUI.depth;
        GUI.matrix = Matrix4x4.identity;
        GUI.depth = -10000;
        float scale = Mathf.Min(1f, Mathf.Min(Screen.width / 360f, Screen.height / 240f));
        scale = Mathf.Max(0.01f, scale);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float screenWidth = Screen.width / scale, screenHeight = Screen.height / scale;
        float width = Mathf.Min(screenWidth - 24f, 720f);
        float height = Mathf.Min(screenHeight - 24f, 520f);
        Rect window = new Rect((screenWidth-width)/2f, (screenHeight-height)/2f, width, height);
        if (failureStyle == null)
            failureStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
        GUI.Box(window, GUIContent.none);
        GUI.BeginGroup(window);
        GUI.Label(new Rect(12, 8, width - 24, 42), "Unable to join - Mod compatibility", failureStyle);
        Rect viewport = new Rect(12, 52, width - 24, height - 106);
        float textWidth = viewport.width - 24;
        float contentHeight = failureStyle.CalcHeight(new GUIContent(lastFailure), textWidth) + 8;
        failureScroll = GUI.BeginScrollView(viewport, failureScroll,
            new Rect(0, 0, textWidth, Mathf.Max(viewport.height, contentHeight)), false, false);
        GUI.Label(new Rect(0, 0, textWidth, contentHeight), lastFailure, failureStyle);
        GUI.EndScrollView();
        if (GUI.Button(new Rect(12, height - 44, width - 24, 32), "Close (Enter)")) lastFailure = "";
        GUI.EndGroup();
        GUI.matrix = oldMatrix;
        GUI.depth = oldDepth;
    }
}
