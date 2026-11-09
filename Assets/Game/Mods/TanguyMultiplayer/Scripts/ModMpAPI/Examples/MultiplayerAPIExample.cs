// Example ordinary DFU mod usage for the Multiplayer API.
// No "using Mirror;" and no NetworkBehaviour are needed here.

using System;
using UnityEngine;
using DaggerfallCoop.API;

public static class MultiplayerAPIExample
{
    const string ModGuid = "example.mod.guid";
    const string PingChannel = "ping";
    const string PongChannel = "pong";

    [Serializable]
    public class Ping
    {
        public int number;
        public string text;
    }

    [Serializable]
    public class Pong
    {
        public uint sender;
        public int number;
    }

    public static void Register()
    {
        MultiplayerAPI.RegisterJsonServerHandler<Ping>(ModGuid, PingChannel, OnServerPing);
        MultiplayerAPI.RegisterJsonClientHandler<Pong>(ModGuid, PongChannel, OnClientPong);
        MultiplayerAPI.PlayerJoined += OnPlayerJoined;
        MultiplayerAPI.PlayerLeft += OnPlayerLeft;
    }

    public static void SendTestPing()
    {
        if (!MultiplayerAPI.IsActive) return;

        MultiplayerAPI.SendJsonToServer(
            ModGuid,
            PingChannel,
            new Ping { number = 42, text = "hello from a DFU mod" });
    }

    static void OnServerPing(ModMessageContext context, Ping ping)
    {
        // Gameplay requests must be validated here on the server.
        if (!MultiplayerAPI.IsPlayerConnected(context.SenderPlayerId)) return;

        Debug.Log("[Example] server got ping " + ping.number +
                  " from player " + context.SenderPlayerId);

        MultiplayerAPI.SendJsonToPlayer(
            context.SenderPlayerId,
            ModGuid,
            PongChannel,
            new Pong { sender = context.SenderPlayerId, number = ping.number });
    }

    static void OnClientPong(ModMessageContext context, Pong pong)
    {
        Debug.Log("[Example] received pong " + pong.number +
                  " for sender " + pong.sender);
    }

    static void OnPlayerJoined(MultiplayerPlayer player)
    {
        Debug.Log("[Example] joined: " + player.NetId + " " + player.Name);
    }

    static void OnPlayerLeft(MultiplayerPlayer player)
    {
        Debug.Log("[Example] left: " + player.NetId + " " + player.Name);
    }
}
