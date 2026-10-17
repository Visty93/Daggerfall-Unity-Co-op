using System;
using System.IO;
using System.Reflection;
using FullSerializer;
using UnityEngine;
using DaggerfallWorkshop;
using Mirror;

// Only loaded by DedicatedServerBootstrap, before listening for connections.
// Separate sections leave room for future admission rules without pretending
// that mod/dungeon enforcement or pre-character connections exist already.
[Serializable]
public sealed class DedicatedServerConfig
{
    public int schemaVersion = 1;
    public NetworkSettings network = new NetworkSettings();
    public WorldSettings world = new WorldSettings();
    public PartyDefaults partyDefaults = new PartyDefaults();
    public MultiplayerSettings multiplayer = new MultiplayerSettings();
    public RespawnSettings respawn = new RespawnSettings();

    [Serializable]
    public sealed class NetworkSettings
    {
        public int port = 7777;
        public int maxPlayers = 100;
    }

    [Serializable]
    public sealed class WorldSettings
    {
        public string dungeonSize = "large";
    }

    [Serializable]
    public sealed class PartyDefaults
    {
        public bool leaderControlsTime = true;
        public string questSync = "full";
    }

    [Serializable]
    public sealed class MultiplayerSettings
    {
        public bool displayNames = true;
        public bool useHighestLevel = false;
        public bool sendLocations = true;
        public bool sendMessages = true;
        public bool mobileNpcSync = true;
        public bool partyTravelInsideDungeon = true;
        // Entire world-container bridge: interiors, dungeons, generic exterior, WoD camps.
        // Does NOT affect networked enemy corpses, quests, or player-dropped loot.
        public bool worldContainerSync = true;
    }

    [Serializable]
    public sealed class RespawnSettings
    {
        public int reviveHoldSeconds = 5;
        public int manualRespawnSeconds = 10;
        public int reviveHealthPercent = 30;
        public int respawnHealthPercent = 30;
        public int reviveFatiguePercent = 30;
        public int respawnFatiguePercent = 30;
        public bool respawnOutsideDungeon = false;
    }

    public static DedicatedServerConfig Load(DfuDedicatedServerOptions options, out string path)
    {
        string baseDirectory = Path.GetDirectoryName(Application.dataPath);
        bool explicitPath = !string.IsNullOrEmpty(options.ConfigPath);
        string requested = explicitPath ? options.ConfigPath : "DedicatedServer.json";
        path = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(baseDirectory, requested));
        if (!File.Exists(path))
        {
            if (explicitPath)
                throw new FileNotFoundException("The -dfu-config file does not exist.", path);
            var defaults = new DedicatedServerConfig();
            // Never overwrite an existing operator's configuration.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.WriteLine(JsonUtility.ToJson(defaults, true));
            Debug.Log("[DFUDedicated] Created default configuration: " + path);
            return defaults;
        }
        return Parse(File.ReadAllText(path));
    }

    public static DedicatedServerConfig Parse(string json)
    {
        var result = new DedicatedServerConfig();
        ApplyObject(result, fsJsonParser.Parse(json), "config");
        result.Validate();
        return result;
    }

    static void ApplyObject(object target, fsData data, string path)
    {
        if (data == null || !data.IsDictionary)
            throw new FormatException(path + " must be a JSON object.");
        foreach (var pair in data.AsDictionary)
        {
            string key = path + "." + pair.Key;
            FieldInfo field = target.GetType().GetField(pair.Key, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) throw new FormatException("Unknown setting: " + key);
            fsData value = pair.Value;
            if (field.FieldType == typeof(bool))
            {
                if (!value.IsBool) throw new FormatException(key + " must be true or false.");
                field.SetValue(target, value.AsBool);
            }
            else if (field.FieldType == typeof(int))
            {
                if (!value.IsInt64 || value.AsInt64 < int.MinValue || value.AsInt64 > int.MaxValue)
                    throw new FormatException(key + " must be a whole number.");
                field.SetValue(target, (int)value.AsInt64);
            }
            else if (field.FieldType == typeof(string))
            {
                if (!value.IsString) throw new FormatException(key + " must be a string.");
                field.SetValue(target, value.AsString);
            }
            else
                ApplyObject(field.GetValue(target), value, key);
        }
    }

    public void Validate()
    {
        if (schemaVersion != 1) throw new FormatException("Unsupported config schemaVersion: " + schemaVersion);
        RequireRange("network.port", network.port, 1, 65535);
        RequireRange("network.maxPlayers", network.maxPlayers, 1, 1024);
        if (world.dungeonSize != "small" && world.dungeonSize != "large")
            throw new FormatException("world.dungeonSize must be \"small\" or \"large\".");
        if (partyDefaults.questSync != "full" && partyDefaults.questSync != "share-only" && partyDefaults.questSync != "off")
            throw new FormatException("partyDefaults.questSync must be \"full\", \"share-only\", or \"off\".");
        RequireRange("respawn.reviveHoldSeconds", respawn.reviveHoldSeconds, 1, 10);
        RequireRange("respawn.manualRespawnSeconds", respawn.manualRespawnSeconds, 5, 30);
        RequireRange("respawn.reviveHealthPercent", respawn.reviveHealthPercent, 10, 50);
        RequireRange("respawn.respawnHealthPercent", respawn.respawnHealthPercent, 10, 50);
        RequireRange("respawn.reviveFatiguePercent", respawn.reviveFatiguePercent, 10, 50);
        RequireRange("respawn.respawnFatiguePercent", respawn.respawnFatiguePercent, 10, 50);
    }

    static void RequireRange(string name, int value, int min, int max)
    {
        if (value < min || value > max)
            throw new FormatException(name + " must be between " + min + " and " + max + ".");
    }

    public void ApplyGameplayOptions()
    {
        Validate();
        OptionsMultiplayer.ApplyDedicatedSettings(this);
        WorldContainerPolicy.SetDedicatedOption(multiplayer.worldContainerSync);
        DaggerfallUnity.Settings.SmallerDungeons = world.dungeonSize == "small";
    }
}
