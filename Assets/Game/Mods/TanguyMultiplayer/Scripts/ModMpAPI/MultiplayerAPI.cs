// Daggerfall Unity Co-op - public multiplayer API v0.5
// Third-party mods do not need to use Mirror directly or be Mirror-woven.
// Runtime implementation of the public multiplayer API.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Mirror;

namespace DaggerfallCoop.API
{
    public struct ModMessageContext
    {
        public uint SenderPlayerId;
        public bool IsServer;
        public bool IsClient;

        public ModMessageContext(uint senderPlayerId, bool isServer, bool isClient)
        {
            SenderPlayerId = senderPlayerId;
            IsServer = isServer;
            IsClient = isClient;
        }
    }

    public struct MultiplayerPlayer
    {
        public uint NetId;
        public string Name;
        public GameObject GameObject;
        public ulong PartyId;
        public uint PartyLeaderNetId;

        public bool IsLocal
        {
            get { return NetId != 0U && NetId == MultiplayerAPI.LocalPlayerId; }
        }
    }

    public static partial class MultiplayerAPI
    {
        public const int ApiVersion = 5;
        public const int MaxPayloadBytes = 32 * 1024;
        public const int MaxGuidLength = 128;
        public const int MaxChannelLength = 64;

        static MultiplayerAPIBridge Bridge
        {
            get { return MultiplayerAPIBridge.EnsureInstalled(); }
        }

        public static bool IsActive { get { return NetworkServer.active || NetworkClient.active; } }
        public static bool IsServer { get { return NetworkServer.active; } }
        public static bool IsClient { get { return NetworkClient.active; } }
        public static bool IsHost { get { return NetworkServer.active && NetworkClient.active; } }

        public static uint LocalPlayerId
        {
            get
            {
                try
                {
                    uint id = global::PlayerMultiplayer.GetLocalPlayerNetIdSafe();
                    if (id != 0U) return id;
                }
                catch { }

                try
                {
                    if (NetworkClient.active && NetworkClient.localPlayer != null)
                        return NetworkClient.localPlayer.netId;
                }
                catch { }

                try
                {
                    if (NetworkServer.active && NetworkServer.localConnection != null &&
                        NetworkServer.localConnection.identity != null)
                        return NetworkServer.localConnection.identity.netId;
                }
                catch { }

                return 0U;
            }
        }

        public static event Action<MultiplayerPlayer> PlayerJoined
        {
            add { Bridge.AddPlayerJoined(value); }
            remove { Bridge.RemovePlayerJoined(value); }
        }

        public static event Action<MultiplayerPlayer> PlayerLeft
        {
            add { Bridge.AddPlayerLeft(value); }
            remove { Bridge.RemovePlayerLeft(value); }
        }

        public static event Action PartyStateChanged
        {
            add { Bridge.AddPartyStateChanged(value); }
            remove { Bridge.RemovePartyStateChanged(value); }
        }

        public static MultiplayerPlayer[] GetPlayers()
        {
            return MultiplayerAPIBridge.BuildPlayerSnapshot();
        }

        public static bool TryGetPlayer(uint playerNetId, out MultiplayerPlayer player)
        {
            MultiplayerPlayer[] players = GetPlayers();
            for (int i = 0; i < players.Length; i++)
            {
                if (players[i].NetId == playerNetId)
                {
                    player = players[i];
                    return true;
                }
            }
            player = default(MultiplayerPlayer);
            return false;
        }

        public static bool IsPlayerConnected(uint playerNetId)
        {
            MultiplayerPlayer ignored;
            return playerNetId != 0U && TryGetPlayer(playerNetId, out ignored);
        }

        public static ulong GetPartyId(uint playerNetId)
        {
            if (playerNetId == 0U) return 0UL;
            global::MultiplayerPartyManager.PartyClientState state;
            return global::MultiplayerPartyManager.TryGetClientPartyState(playerNetId, out state) && state != null
                ? state.PartyId : 0UL;
        }

        public static uint GetPartyLeaderId(uint playerNetId)
        {
            if (playerNetId == 0U) return 0U;
            global::MultiplayerPartyManager.PartyClientState state;
            return global::MultiplayerPartyManager.TryGetClientPartyState(playerNetId, out state) && state != null
                ? state.LeaderNetId : 0U;
        }

        public static bool AreInSameParty(uint firstPlayerNetId, uint secondPlayerNetId)
        {
            return global::MultiplayerPartyManager.AreSameParty(firstPlayerNetId, secondPlayerNetId);
        }

        public static uint[] GetPartyMembers(uint playerNetId)
        {
            ulong partyId = GetPartyId(playerNetId);
            if (partyId == 0UL) return new uint[0];

            global::MultiplayerPartyManager.PartyClientState[] roster =
                global::MultiplayerPartyManager.GetClientRosterSnapshot();

            return roster
                .Where(s => s != null && s.PartyId == partyId && s.NetId != 0U)
                .Select(s => s.NetId)
                .Distinct()
                .ToArray();
        }

        public static bool TryGetNetworkId(GameObject obj, out uint netId)
        {
            netId = 0U;
            if (obj == null) return false;

            NetworkIdentity identity = obj.GetComponent<NetworkIdentity>();
            if (identity == null) identity = obj.GetComponentInParent<NetworkIdentity>();
            if (identity == null || identity.netId == 0U) return false;

            netId = identity.netId;
            return true;
        }

        /// <summary>
        /// Read an existing networked exterior enemy's synchronized Daggerfall world
        /// coordinates, independently of this client's Unity terrain/floating-origin frame.
        /// Returns false if the enemy or its world-position metadata is unavailable,
        /// including interiors and dungeons (whose X/Z may be entrance anchors).
        /// </summary>
        /// <remarks>
        /// The position's WorldX/WorldZ are DF world units (40 units per metre).
        /// Height is not provided by enemy world-position metadata and is set to zero;
        /// use HorizontalDistance() for proximity checks. This is a local metadata
        /// query, not a server-side permission or encounter-authority check.
        /// </remarks>
        public static bool TryGetEnemyWorldPosition(GameObject enemyObject, out MultiplayerWorldPosition position)
        {
            position = default(MultiplayerWorldPosition);
            if (!IsActive || enemyObject == null) return false;

            NetworkIdentity identity = enemyObject.GetComponent<NetworkIdentity>();
            if (identity == null) identity = enemyObject.GetComponentInParent<NetworkIdentity>();
            if (identity == null || identity.netId == 0U) return false;

            // Fork-internal metadata stays inside the API. A mod only sees the
            // public MultiplayerWorldPosition result, not EnemyWorldPosition.
            global::EnemyWorldPosition world = identity.GetComponent<global::EnemyWorldPosition>();
            if (world == null || world.isInteriorSpawn || world.isDungeonSpawn)
                return false;

            // Client SyncVars can be received before local initialization completes.
            // Do not expose the default zero/zero position as a real location.
            if (!world.initialized && !world.worldBakedFromRequester &&
                world.worldX == 0 && world.worldZ == 0)
                return false;

            position = new MultiplayerWorldPosition
            {
                WorldX = world.worldX,
                WorldZ = world.worldZ,
                Height = 0f
            };
            return true;
        }

        public static bool TryGetNetworkObject(uint netId, out GameObject obj)
        {
            obj = null;
            if (netId == 0U) return false;

            NetworkIdentity identity = null;
            if (NetworkClient.active) NetworkClient.spawned.TryGetValue(netId, out identity);
            if (identity == null && NetworkServer.active) NetworkServer.spawned.TryGetValue(netId, out identity);
            if (identity == null) return false;

            obj = identity.gameObject;
            return obj != null;
        }

        // v0.1 intentionally only accepts an already-networked object.
        public static bool RegisterNetworkObject(GameObject obj, out uint netId)
        {
            return TryGetNetworkId(obj, out netId);
        }

        public static bool IsAuthoritativeFor(GameObject obj)
        {
            if (obj == null) return false;

            NetworkIdentity identity = obj.GetComponent<NetworkIdentity>();
            if (identity == null) identity = obj.GetComponentInParent<NetworkIdentity>();
            if (identity == null || identity.netId == 0U) return false;

            if (NetworkClient.active && identity.hasAuthority) return true;
            if (NetworkServer.active && identity.connectionToClient == null) return true;
            return false;
        }

        public static uint GetOwnerPlayerId(GameObject obj)
        {
            if (obj == null) return 0U;

            NetworkIdentity identity = obj.GetComponent<NetworkIdentity>();
            if (identity == null) identity = obj.GetComponentInParent<NetworkIdentity>();
            if (identity == null) return 0U;

            if (NetworkServer.active && identity.connectionToClient != null &&
                identity.connectionToClient.identity != null)
                return identity.connectionToClient.identity.netId;

            if (NetworkClient.active && identity.hasAuthority)
                return LocalPlayerId;

            return 0U;
        }

        public static bool RegisterServerHandler(string modGuid, string channel,
            Action<ModMessageContext, byte[]> handler)
        {
            return Bridge.RegisterServerHandler(modGuid, channel, handler);
        }

        public static bool RegisterClientHandler(string modGuid, string channel,
            Action<ModMessageContext, byte[]> handler)
        {
            return Bridge.RegisterClientHandler(modGuid, channel, handler);
        }

        public static void UnregisterServerHandler(string modGuid, string channel)
        {
            Bridge.UnregisterServerHandler(modGuid, channel);
        }

        public static void UnregisterClientHandler(string modGuid, string channel)
        {
            Bridge.UnregisterClientHandler(modGuid, channel);
        }

        public static bool SendToServer(string modGuid, string channel, byte[] payload)
        {
            return Bridge.SendToServer(modGuid, channel, payload);
        }

        public static bool SendToAll(string modGuid, string channel, byte[] payload)
        {
            return Bridge.SendToAll(modGuid, channel, payload);
        }

        public static bool SendToPlayer(uint playerNetId, string modGuid, string channel, byte[] payload)
        {
            return Bridge.SendToPlayer(playerNetId, modGuid, channel, payload);
        }

        public static bool SendToParty(uint referencePlayerNetId, string modGuid, string channel, byte[] payload)
        {
            return Bridge.SendToParty(referencePlayerNetId, modGuid, channel, payload);
        }

        public static byte[] ToJsonPayload<T>(T value)
        {
            string json = JsonUtility.ToJson(value);
            return Encoding.UTF8.GetBytes(json ?? string.Empty);
        }

        public static T FromJsonPayload<T>(byte[] payload)
        {
            string json = payload == null ? string.Empty : Encoding.UTF8.GetString(payload);
            return JsonUtility.FromJson<T>(json);
        }

        public static bool SendJsonToServer<T>(string modGuid, string channel, T value)
        {
            return SendToServer(modGuid, channel, ToJsonPayload(value));
        }

        public static bool SendJsonToAll<T>(string modGuid, string channel, T value)
        {
            return SendToAll(modGuid, channel, ToJsonPayload(value));
        }

        public static bool SendJsonToPlayer<T>(uint playerNetId, string modGuid, string channel, T value)
        {
            return SendToPlayer(playerNetId, modGuid, channel, ToJsonPayload(value));
        }

        public static bool SendJsonToParty<T>(uint referencePlayerNetId, string modGuid, string channel, T value)
        {
            return SendToParty(referencePlayerNetId, modGuid, channel, ToJsonPayload(value));
        }

        public static bool RegisterJsonServerHandler<T>(string modGuid, string channel,
            Action<ModMessageContext, T> handler)
        {
            if (handler == null) return false;
            return RegisterServerHandler(modGuid, channel, delegate(ModMessageContext context, byte[] payload)
            {
                T value;
                try { value = FromJsonPayload<T>(payload); }
                catch (Exception e)
                {
                    Debug.LogWarning("[MPAPI] Could not decode JSON for " + modGuid + "/" + channel + ": " + e.Message);
                    return;
                }
                handler(context, value);
            });
        }

        public static bool RegisterJsonClientHandler<T>(string modGuid, string channel,
            Action<ModMessageContext, T> handler)
        {
            if (handler == null) return false;
            return RegisterClientHandler(modGuid, channel, delegate(ModMessageContext context, byte[] payload)
            {
                T value;
                try { value = FromJsonPayload<T>(payload); }
                catch (Exception e)
                {
                    Debug.LogWarning("[MPAPI] Could not decode JSON for " + modGuid + "/" + channel + ": " + e.Message);
                    return;
                }
                handler(context, value);
            });
        }
    }

    // One fixed Mirror message owned by the co-op build.
    public struct MultiplayerAPIModEnvelope : NetworkMessage
    {
        public string modGuid;
        public string channel;
        public byte[] payload;
    }

    [DefaultExecutionOrder(-25000)]
    internal sealed partial class MultiplayerAPIBridge : MonoBehaviour
    {
        const string Prefix = "[MPAPI] ";
        const float PlayerScanInterval = 0.25f;
        const int MaxMessagesPerSecondPerConnection = 128;

        static MultiplayerAPIBridge instance;

        readonly Dictionary<string, Action<ModMessageContext, byte[]>> serverHandlers =
            new Dictionary<string, Action<ModMessageContext, byte[]>>(StringComparer.Ordinal);
        readonly Dictionary<string, Action<ModMessageContext, byte[]>> clientHandlers =
            new Dictionary<string, Action<ModMessageContext, byte[]>>(StringComparer.Ordinal);
        readonly Dictionary<int, RateCounter> serverRates = new Dictionary<int, RateCounter>();
        readonly Dictionary<uint, MultiplayerPlayer> knownPlayers = new Dictionary<uint, MultiplayerPlayer>();

        Action<MultiplayerPlayer> playerJoined;
        Action<MultiplayerPlayer> playerLeft;
        Action partyStateChanged;
        bool serverHandlerInstalled;
        bool clientHandlerInstalled;
        float nextPlayerScan;

        sealed class RateCounter
        {
            public float since;
            public int count;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            EnsureInstalled();
        }

        internal static MultiplayerAPIBridge EnsureInstalled()
        {
            if (instance != null) return instance;

            MultiplayerAPIBridge existing = UnityEngine.Object.FindObjectOfType<MultiplayerAPIBridge>();
            if (existing != null)
            {
                instance = existing;
                return instance;
            }

            GameObject go = new GameObject("Daggerfall Co-op Multiplayer API");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MultiplayerAPIBridge>();
            return instance;
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            global::Mirror.NetworkManager.ClientExtensionsReady += InstallClientHandler;
            global::Mirror.NetworkManager.ServerExtensionsReady += InstallServerHandler;
            global::Mirror.NetworkManager.SessionStopped += OnSessionStopped;
            global::MultiplayerPartyManager.PartyStateChanged += OnPartyStateChanged;

            if (NetworkServer.active) InstallServerHandler();
            if (NetworkClient.active) InstallClientHandler();
        }

        void OnDestroy()
        {
            if (instance != this) return;
            global::Mirror.NetworkManager.ClientExtensionsReady -= InstallClientHandler;
            global::Mirror.NetworkManager.ServerExtensionsReady -= InstallServerHandler;
            global::Mirror.NetworkManager.SessionStopped -= OnSessionStopped;
            global::MultiplayerPartyManager.PartyStateChanged -= OnPartyStateChanged;
            instance = null;
        }

        void Update()
        {
            TickState();
            if (NetworkServer.active && !serverHandlerInstalled) InstallServerHandler();
            if (NetworkClient.active && !clientHandlerInstalled) InstallClientHandler();

            if (!MultiplayerAPI.IsActive)
            {
                if (knownPlayers.Count != 0) ClearKnownPlayers(true);
                return;
            }

            if (Time.unscaledTime >= nextPlayerScan)
            {
                nextPlayerScan = Time.unscaledTime + PlayerScanInterval;
                RefreshKnownPlayers();
            }
        }

        void InstallServerHandler()
        {
            NetworkServer.ReplaceHandler<MultiplayerAPIModEnvelope>(OnServerEnvelope);
            InstallStateServerHandler();
            serverHandlerInstalled = true;
            Debug.Log(Prefix + "Server mod-message handler installed.");
        }

        void InstallClientHandler()
        {
            NetworkClient.ReplaceHandler<MultiplayerAPIModEnvelope>(OnClientEnvelope);
            InstallStateClientHandler();
            clientHandlerInstalled = true;
            Debug.Log(Prefix + "Client mod-message handler installed.");
        }

        void OnSessionStopped()
        {
            ResetStateSession();
            serverHandlerInstalled = false;
            clientHandlerInstalled = false;
            serverRates.Clear();
            ClearKnownPlayers(true);
        }

        void OnPartyStateChanged()
        {
            RefreshKnownPlayers();
            Action callback = partyStateChanged;
            if (callback != null)
            {
                try { callback(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        internal void AddPlayerJoined(Action<MultiplayerPlayer> value) { playerJoined += value; }
        internal void RemovePlayerJoined(Action<MultiplayerPlayer> value) { playerJoined -= value; }
        internal void AddPlayerLeft(Action<MultiplayerPlayer> value) { playerLeft += value; }
        internal void RemovePlayerLeft(Action<MultiplayerPlayer> value) { playerLeft -= value; }
        internal void AddPartyStateChanged(Action value) { partyStateChanged += value; }
        internal void RemovePartyStateChanged(Action value) { partyStateChanged -= value; }

        static string Key(string modGuid, string channel)
        {
            return (modGuid ?? string.Empty) + "\n" + (channel ?? string.Empty);
        }

        static bool ValidAddress(string modGuid, string channel)
        {
            return !string.IsNullOrEmpty(modGuid) && modGuid.Length <= MultiplayerAPI.MaxGuidLength && modGuid.IndexOf('\n') < 0 &&
                   modGuid.IndexOf('\r') < 0 &&
                   !string.IsNullOrEmpty(channel) && channel.Length <= MultiplayerAPI.MaxChannelLength && channel.IndexOf('\n') < 0 && channel.IndexOf('\r') < 0;
        }

        static bool ValidPayload(byte[] payload)
        {
            return payload == null || payload.Length <= MultiplayerAPI.MaxPayloadBytes;
        }

        internal bool RegisterServerHandler(string modGuid, string channel,
            Action<ModMessageContext, byte[]> handler)
        {
            if (!ValidAddress(modGuid, channel) || handler == null) return false;
            string key = Key(modGuid, channel);
            if (serverHandlers.ContainsKey(key))
            {
                Debug.LogWarning(Prefix + "Server handler already registered for " + modGuid + "/" + channel + ".");
                return false;
            }
            serverHandlers.Add(key, handler);
            return true;
        }

        internal bool RegisterClientHandler(string modGuid, string channel,
            Action<ModMessageContext, byte[]> handler)
        {
            if (!ValidAddress(modGuid, channel) || handler == null) return false;
            string key = Key(modGuid, channel);
            if (clientHandlers.ContainsKey(key))
            {
                Debug.LogWarning(Prefix + "Client handler already registered for " + modGuid + "/" + channel + ".");
                return false;
            }
            clientHandlers.Add(key, handler);
            return true;
        }

        internal void UnregisterServerHandler(string modGuid, string channel)
        {
            serverHandlers.Remove(Key(modGuid, channel));
        }

        internal void UnregisterClientHandler(string modGuid, string channel)
        {
            clientHandlers.Remove(Key(modGuid, channel));
        }

        internal bool SendToServer(string modGuid, string channel, byte[] payload)
        {
            if (!NetworkClient.active || !NetworkClient.isConnected || !NetworkClient.ready ||
                !ValidAddress(modGuid, channel) || !ValidPayload(payload)) return false;

            NetworkClient.Send(new MultiplayerAPIModEnvelope
            {
                modGuid = modGuid,
                channel = channel,
                payload = payload ?? new byte[0]
            });
            return true;
        }

        internal bool SendToAll(string modGuid, string channel, byte[] payload)
        {
            if (!NetworkServer.active || !ValidAddress(modGuid, channel) || !ValidPayload(payload)) return false;

            NetworkServer.SendToAll(new MultiplayerAPIModEnvelope
            {
                modGuid = modGuid,
                channel = channel,
                payload = payload ?? new byte[0]
            }, Channels.Reliable, true);
            return true;
        }

        internal bool SendToPlayer(uint playerNetId, string modGuid, string channel, byte[] payload)
        {
            if (!NetworkServer.active || playerNetId == 0U ||
                !ValidAddress(modGuid, channel) || !ValidPayload(payload)) return false;

            foreach (NetworkConnection connection in NetworkServer.connections.Values)
            {
                if (connection == null || !connection.isReady || connection.identity == null ||
                    connection.identity.netId != playerNetId) continue;

                connection.Send(new MultiplayerAPIModEnvelope
                {
                    modGuid = modGuid,
                    channel = channel,
                    payload = payload ?? new byte[0]
                });
                return true;
            }
            return false;
        }

        internal bool SendToParty(uint referencePlayerNetId, string modGuid, string channel, byte[] payload)
        {
            if (!NetworkServer.active || referencePlayerNetId == 0U ||
                !ValidAddress(modGuid, channel) || !ValidPayload(payload)) return false;

            ulong partyId = MultiplayerAPI.GetPartyId(referencePlayerNetId);
            if (partyId == 0UL) return false;

            bool sent = false;
            foreach (NetworkConnection connection in NetworkServer.connections.Values)
            {
                if (connection == null || !connection.isReady || connection.identity == null) continue;
                uint playerNetId = connection.identity.netId;
                if (MultiplayerAPI.GetPartyId(playerNetId) != partyId) continue;

                connection.Send(new MultiplayerAPIModEnvelope
                {
                    modGuid = modGuid,
                    channel = channel,
                    payload = payload ?? new byte[0]
                });
                sent = true;
            }
            return sent;
        }

        void OnServerEnvelope(NetworkConnection sender, MultiplayerAPIModEnvelope message)
        {
            if (sender == null || !sender.isAuthenticated || !sender.isReady || sender.identity == null ||
                sender.identity.GetComponent<global::PlayerMultiplayer>() == null) return;
            if (!ValidAddress(message.modGuid, message.channel) || !ValidPayload(message.payload)) return;

            RateCounter rate;
            if (!serverRates.TryGetValue(sender.connectionId, out rate))
            {
                rate = new RateCounter { since = Time.unscaledTime, count = 0 };
                serverRates.Add(sender.connectionId, rate);
            }
            if (Time.unscaledTime - rate.since >= 1f)
            {
                rate.since = Time.unscaledTime;
                rate.count = 0;
            }
            if (++rate.count > MaxMessagesPerSecondPerConnection) return;

            Action<ModMessageContext, byte[]> handler;
            if (!serverHandlers.TryGetValue(Key(message.modGuid, message.channel), out handler) || handler == null) return;

            ModMessageContext context = new ModMessageContext(sender.identity.netId, true, NetworkClient.active);
            try { handler(context, message.payload ?? new byte[0]); }
            catch (Exception e)
            {
                Debug.LogError(Prefix + "Server handler failed for " + message.modGuid + "/" + message.channel + ": " + e);
            }
        }

        void OnClientEnvelope(MultiplayerAPIModEnvelope message)
        {
            if (!ValidAddress(message.modGuid, message.channel) || !ValidPayload(message.payload)) return;

            Action<ModMessageContext, byte[]> handler;
            if (!clientHandlers.TryGetValue(Key(message.modGuid, message.channel), out handler) || handler == null) return;

            ModMessageContext context = new ModMessageContext(0U, NetworkServer.active, true);
            try { handler(context, message.payload ?? new byte[0]); }
            catch (Exception e)
            {
                Debug.LogError(Prefix + "Client handler failed for " + message.modGuid + "/" + message.channel + ": " + e);
            }
        }

        internal static MultiplayerPlayer[] BuildPlayerSnapshot()
        {
            Dictionary<uint, MultiplayerPlayer> result = new Dictionary<uint, MultiplayerPlayer>();
            global::MultiplayerPartyManager.PartyClientState[] roster =
                global::MultiplayerPartyManager.GetClientRosterSnapshot();

            for (int i = 0; i < roster.Length; i++)
            {
                global::MultiplayerPartyManager.PartyClientState state = roster[i];
                if (state == null || state.NetId == 0U) continue;

                GameObject root = null;
                MultiplayerAPI.TryGetNetworkObject(state.NetId, out root);
                result[state.NetId] = new MultiplayerPlayer
                {
                    NetId = state.NetId,
                    Name = state.PlayerName,
                    GameObject = root,
                    PartyId = state.PartyId,
                    PartyLeaderNetId = state.LeaderNetId
                };
            }

            if (NetworkClient.active)
                foreach (NetworkIdentity identity in NetworkClient.spawned.Values)
                    AddIdentityPlayer(result, identity);

            if (NetworkServer.active)
                foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
                    AddIdentityPlayer(result, identity);

            return result.Values.OrderBy(p => p.NetId).ToArray();
        }

        static void AddIdentityPlayer(Dictionary<uint, MultiplayerPlayer> result, NetworkIdentity identity)
        {
            if (identity == null || identity.netId == 0U) return;
            global::PlayerMultiplayer player = identity.GetComponent<global::PlayerMultiplayer>();
            if (player == null) return;

            MultiplayerPlayer existing;
            if (result.TryGetValue(identity.netId, out existing))
            {
                if (existing.GameObject == null)
                {
                    existing.GameObject = identity.gameObject;
                    result[identity.netId] = existing;
                }
                return;
            }

            global::MultiplayerPartyManager.PartyClientState state;
            bool hasParty = global::MultiplayerPartyManager.TryGetClientPartyState(identity.netId, out state) && state != null;

            result.Add(identity.netId, new MultiplayerPlayer
            {
                NetId = identity.netId,
                Name = hasParty ? state.PlayerName : identity.gameObject.name,
                GameObject = identity.gameObject,
                PartyId = hasParty ? state.PartyId : 0UL,
                PartyLeaderNetId = hasParty ? state.LeaderNetId : 0U
            });
        }

        void RefreshKnownPlayers()
        {
            MultiplayerPlayer[] current = BuildPlayerSnapshot();
            HashSet<uint> seen = new HashSet<uint>();

            for (int i = 0; i < current.Length; i++)
            {
                MultiplayerPlayer player = current[i];
                if (player.NetId == 0U) continue;

                seen.Add(player.NetId);
                if (!knownPlayers.ContainsKey(player.NetId))
                {
                    knownPlayers.Add(player.NetId, player);
                    Action<MultiplayerPlayer> callback = playerJoined;
                    if (callback != null)
                    {
                        try { callback(player); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                }
                else knownPlayers[player.NetId] = player;
            }

            List<uint> gone = null;
            foreach (KeyValuePair<uint, MultiplayerPlayer> pair in knownPlayers)
            {
                if (seen.Contains(pair.Key)) continue;
                if (gone == null) gone = new List<uint>();
                gone.Add(pair.Key);
            }
            if (gone == null) return;

            for (int i = 0; i < gone.Count; i++)
            {
                MultiplayerPlayer player = knownPlayers[gone[i]];
                knownPlayers.Remove(gone[i]);
                Action<MultiplayerPlayer> callback = playerLeft;
                if (callback != null)
                {
                    try { callback(player); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
        }

        void ClearKnownPlayers(bool raiseLeft)
        {
            if (raiseLeft && playerLeft != null)
            {
                foreach (MultiplayerPlayer player in knownPlayers.Values)
                {
                    try { playerLeft(player); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
            knownPlayers.Clear();
        }
    }
}

namespace DaggerfallCoop.API
{
    // Logical mod state, not a Mirror object or authority assignment.
    public struct ModStateRecord
    {
        public string Key;
        public ulong Revision;
        public uint OwnerPlayerId; // 0 = server-controlled; informational metadata.
        public byte[] Payload;
    }
    public struct ModStateChange
    {
        public ModStateRecord Record;
        public bool Removed;
        public bool IsSnapshot;
        public uint SnapshotId;
    }
    public struct ModStateSnapshot
    {
        public uint SnapshotId;
        public string[] Keys; // Complete key set at the captured server revision.
    }
    [Serializable]
    public struct MultiplayerWorldPosition
    {
        public double WorldX, WorldZ;
        public float Height;
        // DF coordinates use 40 units/metre. Height is relative to world compensation.
        public static MultiplayerWorldPosition FromLocal(Vector3 point, Vector3 referenceLocal,
            double referenceWorldX, double referenceWorldZ, float compensationY)
        {
            return new MultiplayerWorldPosition { WorldX = referenceWorldX + (point.x-referenceLocal.x)*40.0,
                WorldZ = referenceWorldZ + (point.z-referenceLocal.z)*40.0, Height = point.y-compensationY };
        }
        public Vector3 ToLocal(Vector3 referenceLocal, double referenceWorldX, double referenceWorldZ, float compensationY)
        {
            return new Vector3(referenceLocal.x+(float)((WorldX-referenceWorldX)/40.0),
                Height+compensationY, referenceLocal.z+(float)((WorldZ-referenceWorldZ)/40.0));
        }
        public double HorizontalDistance(MultiplayerWorldPosition other)
        {
            double x=(WorldX-other.WorldX)/40.0, z=(WorldZ-other.WorldZ)/40.0;
            return Math.Sqrt(x*x+z*z);
        }
    }
    public static partial class MultiplayerAPI
    {
        public const int MaxStateRecords = 2048;
        public const int MaxStateBytes = 8*1024*1024;
        // Public world state only. Do not publish private quest/party/inventory data here.
        // Server decides all writes and ownership after validating custom client requests.
        // expectedRevision: 0 = create-only, ulong.MaxValue = unconditional server write.
        public static bool SetState(string modGuid, string key, byte[] payload, uint ownerPlayerId,
            ulong expectedRevision, out ulong revision)
        { return Bridge.SetState(modGuid,key,payload,ownerPlayerId,expectedRevision,out revision); }
        public static bool RemoveState(string modGuid, string key, ulong expectedRevision)
        { return Bridge.RemoveState(modGuid,key,expectedRevision); }
        public static bool TryGetState(string modGuid, string key, out ModStateRecord record)
        { return Bridge.TryGetState(modGuid,key,out record); } // server only, defensive copy
        // Replays a bounded snapshot automatically when the client becomes ready.
        // SnapshotCompleted supplies the exact key set for deleting stale local replicas.
        public static bool SubscribeState(string modGuid, Action<ModStateChange> changed,
            Action<ModStateSnapshot> snapshotCompleted)
        { return Bridge.SubscribeState(modGuid,changed,snapshotCompleted); }
        public static void UnsubscribeState(string modGuid) { Bridge.UnsubscribeState(modGuid); }
        public static bool RequestStateSnapshot(string modGuid) { return Bridge.RequestStateSnapshot(modGuid); }
        // Explicitly replay/reconcile after mod restoration or fast travel has settled.
        // No save file contents or Unity scene references are stored by the API.
        public static event Action SessionEnded
        {
            add { Bridge.sessionEnded += value; }
            remove { Bridge.sessionEnded -= value; }
        }
    }
    public struct MultiplayerAPIStateEnvelope : NetworkMessage
    {
        public string modGuid, key;
        public byte operation; // 1 subscribe/snapshot, 2 unsubscribe, 3 upsert, 4 remove, 5 begin, 6 end
        public ulong revision;
        public uint owner, snapshot;
        public byte[] payload;
    }
    internal sealed partial class MultiplayerAPIBridge
    {
        sealed class StateSubscription
        {
            public Action<ModStateChange> Changed;
            public Action<ModStateSnapshot> Complete;
            public bool Requested;
            public float RetryAt;
            public uint Snapshot;
            public readonly List<string> Keys = new List<string>();
        }
        sealed class StatePeer
        {
            public readonly HashSet<string> Mods = new HashSet<string>(StringComparer.Ordinal);
            public readonly Queue<MultiplayerAPIStateEnvelope> Pending = new Queue<MultiplayerAPIStateEnvelope>();
            public float LastRequest = -100;
            public int PendingBytes;
        }
        readonly Dictionary<string, ModStateRecord> retainedState = new Dictionary<string, ModStateRecord>();
        readonly Dictionary<string, StateSubscription> stateSubscriptions = new Dictionary<string, StateSubscription>();
        readonly Dictionary<NetworkConnection, StatePeer> statePeers = new Dictionary<NetworkConnection, StatePeer>();
        internal Action sessionEnded;
        int stateBytes;
        ulong stateRevision;
        uint stateSnapshot;
        bool stateClientReady;
        static byte[] CopyBytes(byte[] bytes) { return bytes == null ? new byte[0] : (byte[])bytes.Clone(); }
        static ModStateRecord CopyRecord(ModStateRecord record) { record.Payload=CopyBytes(record.Payload); return record; }
        internal bool TryGetState(string mod, string key, out ModStateRecord record)
        {
            record=default(ModStateRecord);
            if (!NetworkServer.active || !ValidAddress(mod,key) || !retainedState.TryGetValue(Key(mod,key),out record)) return false;
            record=CopyRecord(record); return true;
        }
        internal bool SetState(string mod,string key,byte[] payload,uint owner,ulong expected,out ulong revision)
        {
            revision=0;
            if (!NetworkServer.active || !ValidAddress(mod,key) || !ValidPayload(payload)) return false;
            ModStateRecord old; bool exists=retainedState.TryGetValue(Key(mod,key),out old);
            if (expected!=ulong.MaxValue && expected!=(exists?old.Revision:0UL)) return false;
            int bytes=payload==null?0:payload.Length;
            if ((!exists && retainedState.Count>=MultiplayerAPI.MaxStateRecords) ||
                stateBytes-(exists?old.Payload.Length:0)+bytes>MultiplayerAPI.MaxStateBytes ||
                stateRevision==ulong.MaxValue) return false;
            revision=++stateRevision;
            var record=new ModStateRecord{Key=key,Revision=revision,OwnerPlayerId=owner,Payload=CopyBytes(payload)};
            retainedState[Key(mod,key)]=record; stateBytes+=bytes-(exists?old.Payload.Length:0);
            QueueState(new MultiplayerAPIStateEnvelope{modGuid=mod,key=key,operation=3,revision=revision,owner=owner,payload=record.Payload});
            return true;
        }
        internal bool RemoveState(string mod,string key,ulong expected)
        {
            ModStateRecord old;
            if (!NetworkServer.active || !ValidAddress(mod,key) || !retainedState.TryGetValue(Key(mod,key),out old) ||
                (expected!=ulong.MaxValue && expected!=old.Revision) || stateRevision==ulong.MaxValue) return false;
            retainedState.Remove(Key(mod,key)); stateBytes-=old.Payload.Length;
            QueueState(new MultiplayerAPIStateEnvelope{modGuid=mod,key=key,operation=4,revision=++stateRevision});
            return true;
        }
        internal bool SubscribeState(string mod,Action<ModStateChange> changed,Action<ModStateSnapshot> complete)
        {
            if (!ValidAddress(mod,"state") || changed==null || complete==null ||
                stateSubscriptions.ContainsKey(mod) || stateSubscriptions.Count>=64) return false;
            stateSubscriptions.Add(mod,new StateSubscription{Changed=changed,Complete=complete}); return true;
        }
        internal void UnsubscribeState(string mod)
        {
            if (!stateSubscriptions.Remove(mod)) return;
            if (NetworkClient.active && NetworkClient.ready)
                NetworkClient.Send(new MultiplayerAPIStateEnvelope{modGuid=mod,operation=2});
        }
        internal bool RequestStateSnapshot(string mod)
        {
            StateSubscription sub;
            if (!stateSubscriptions.TryGetValue(mod,out sub) || !NetworkClient.active ||
                !NetworkClient.isConnected || !NetworkClient.ready) return false;
            sub.Requested=false; sub.RetryAt=Time.unscaledTime+5f;
            NetworkClient.Send(new MultiplayerAPIStateEnvelope{modGuid=mod,operation=1}); return true;
        }
        void InstallStateServerHandler() { NetworkServer.ReplaceHandler<MultiplayerAPIStateEnvelope>(OnStateServer); }
        void InstallStateClientHandler() { NetworkClient.ReplaceHandler<MultiplayerAPIStateEnvelope>(OnStateClient); }
        void OnStateServer(NetworkConnection sender, MultiplayerAPIStateEnvelope message)
        {
            if (sender==null || !sender.isAuthenticated || !sender.isReady || sender.identity==null ||
                sender.identity.GetComponent<global::PlayerMultiplayer>()==null ||
                !ValidAddress(message.modGuid,"state")) return;
            StatePeer peer;
            if (!statePeers.TryGetValue(sender,out peer))
            { peer=new StatePeer(); statePeers.Add(sender,peer); }
            if (message.operation==2) { peer.Mods.Remove(message.modGuid); return; }
            if (message.operation!=1) return; // Clients cannot write records or owner metadata.
            // One global snapshot request per connection per second; busy clients retry below.
            if (Time.unscaledTime-peer.LastRequest<1f || peer.Pending.Count>4096 || peer.PendingBytes>MultiplayerAPI.MaxStateBytes) return;
            if (!peer.Mods.Contains(message.modGuid) && peer.Mods.Count>=64) return;
            peer.LastRequest=Time.unscaledTime; peer.Mods.Add(message.modGuid);
            if (++stateSnapshot==0) ++stateSnapshot;
            uint snapshot=stateSnapshot;
            peer.Pending.Enqueue(new MultiplayerAPIStateEnvelope{modGuid=message.modGuid,operation=5,snapshot=snapshot});
            string prefix=message.modGuid+"\n";
            foreach (var pair in retainedState)
                if (pair.Key.StartsWith(prefix,StringComparison.Ordinal))
                {
                    var record=pair.Value;
                    peer.PendingBytes += record.Payload.Length;
                    peer.Pending.Enqueue(new MultiplayerAPIStateEnvelope{modGuid=message.modGuid,key=record.Key,
                        operation=3,snapshot=snapshot,revision=record.Revision,owner=record.OwnerPlayerId,payload=record.Payload});
                }
            peer.Pending.Enqueue(new MultiplayerAPIStateEnvelope{modGuid=message.modGuid,operation=6,snapshot=snapshot});
        }
        void QueueState(MultiplayerAPIStateEnvelope message)
        {
            foreach (var pair in statePeers)
            {
                var peer=pair.Value;
                if (!peer.Mods.Contains(message.modGuid)) continue;
                // Bound backlog. A reset asks the client to discard an incomplete
                // snapshot and re-request current retained state after falling behind.
                if (peer.Pending.Count>=8192 || peer.PendingBytes+(message.payload==null?0:message.payload.Length)>2*MultiplayerAPI.MaxStateBytes)
                {
                    peer.Pending.Clear(); peer.PendingBytes=0; peer.Mods.Clear();
                    peer.Pending.Enqueue(new MultiplayerAPIStateEnvelope{operation=7});
                }
                else { peer.Pending.Enqueue(message); peer.PendingBytes+=message.payload==null?0:message.payload.Length; }
            }
        }
        void OnStateClient(MultiplayerAPIStateEnvelope message)
        {
            if (message.operation==7)
            { foreach(var sub in stateSubscriptions.Values){sub.Requested=false;sub.RetryAt=0;sub.Snapshot=0;sub.Keys.Clear();} return; }
            StateSubscription subscription;
            if (!ValidAddress(message.modGuid,"state") || !stateSubscriptions.TryGetValue(message.modGuid,out subscription)) return;
            try
            {
                if (message.operation==5)
                { subscription.RetryAt=Time.unscaledTime+5f; subscription.Snapshot=message.snapshot; subscription.Keys.Clear(); return; }
                if (message.operation==6)
                {
                    if (subscription.Snapshot!=message.snapshot || message.snapshot==0) return;
                    subscription.Requested=true;
                    subscription.Complete(new ModStateSnapshot{SnapshotId=message.snapshot,Keys=subscription.Keys.ToArray()});
                    subscription.Snapshot=0; subscription.Keys.Clear(); return;
                }
                if ((message.operation!=3 && message.operation!=4) || !ValidAddress(message.modGuid,message.key) || !ValidPayload(message.payload)) return;
                if (message.snapshot!=0 && subscription.Snapshot!=message.snapshot) return;
                if (message.snapshot!=0)
                {
                    subscription.RetryAt=Time.unscaledTime+5f;
                    subscription.Keys.Add(message.key);
                }
                subscription.Changed(new ModStateChange{Record=new ModStateRecord{Key=message.key,
                    Revision=message.revision,OwnerPlayerId=message.owner,Payload=CopyBytes(message.payload)},
                    Removed=message.operation==4,IsSnapshot=message.snapshot!=0,SnapshotId=message.snapshot});
            }
            catch(Exception e) { Debug.LogException(e); }
        }
        float nextStateRequest;
        void TickState()
        {
            bool ready=NetworkClient.active && NetworkClient.isConnected && NetworkClient.ready;
            if (!ready && stateClientReady)
                foreach(var sub in stateSubscriptions.Values){sub.Requested=false;sub.RetryAt=0;sub.Snapshot=0;sub.Keys.Clear();}
            stateClientReady=ready;
            // Serial requests avoid exhausting the server's snapshot request budget.
            if (ready && Time.unscaledTime>=nextStateRequest)
            {
                foreach(var pair in stateSubscriptions)
                    if (!pair.Value.Requested && Time.unscaledTime>=pair.Value.RetryAt)
                    { RequestStateSnapshot(pair.Key); nextStateRequest=Time.unscaledTime+1.1f; break; }
            }
            foreach(var pair in statePeers.ToArray())
            {
                var connection=pair.Key; var peer=pair.Value;
                if (!NetworkServer.active || connection==null || !connection.isReady ||
                    !NetworkServer.connections.Values.Contains(connection))
                { statePeers.Remove(connection); continue; }
                for(int i=0;i<8 && peer.Pending.Count>0;i++)
                {
                    var message=peer.Pending.Dequeue();
                    peer.PendingBytes-=message.payload==null?0:message.payload.Length;
                    connection.Send(message);
                }
            }
        }
        void ResetStateSession()
        {
            retainedState.Clear();statePeers.Clear();stateBytes=0;stateRevision=0;stateSnapshot=0;
            stateClientReady=false;nextStateRequest=0;
            foreach(var sub in stateSubscriptions.Values){sub.Requested=false;sub.RetryAt=0;sub.Snapshot=0;sub.Keys.Clear();}
            if(sessionEnded!=null) foreach(Action callback in sessionEnded.GetInvocationList())
                try{callback();}catch(Exception e){Debug.LogException(e);}
        }
    }
}
