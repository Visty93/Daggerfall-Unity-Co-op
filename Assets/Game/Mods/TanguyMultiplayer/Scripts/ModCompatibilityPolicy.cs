using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallWorkshop.Game.Utility.ModSupport;

// The server compatibility policy is intentionally independent of DFU's Mods.json.
// It never enables, disables or downloads third-party code.
[Serializable]
public sealed class ModCompatibilityPolicy
{
    public int schemaVersion = 1;
    public string mode = "relaxed"; // relaxed | approved | exact
    public bool matchVersions = true;
    public bool matchLoadOrder = false;
    public ModRule[] rules = new ModRule[0];

    [Serializable]
    public sealed class ModRule
    {
        public string fileName = ""; // .dfmod filename without extension (platform-independent when same)
        public string guid = "";     // Optional DFU mod GUID; better across platforms
        public string version = "";  // Optional exact required version (if server has no copy)
        public string status = "unspecified"; // required | allowed | blocked | unspecified
    }

    [Serializable]
    public struct ModDescriptor
    {
        public string fileName;
        public string guid;
        public string version;
        public int loadPriority;
    }

    public static string Normalize(string s) { return (s ?? "").Trim().ToLowerInvariant(); }
    static bool ValidGuid(string s) { return !string.IsNullOrEmpty(s) && Normalize(s) != "invalid"; }

    [Serializable]
    public sealed class Manifest
    {
        public ModDescriptor[] mods;
    }

    // This is the disk inventory for the external native server manager, not a
    // network manifest. Collect *all* known mods while disabled mods are still
    // visible in ModManager (before its Start-state Init removes them).
    [Serializable]
    public sealed class InventoryEntry
    {
        public string fileName;
        public string title;
        public string guid;
        public string version;
        public int loadPriority;
        public bool enabled;
    }

    [Serializable]
    sealed class InventorySnapshot
    {
        public int schemaVersion = 2;
        public string generatedUtc;
        public InventoryEntry[] mods;
    }

    public static bool ExportServerInventory(string directory)
    {
        ModManager mm = ModManager.Instance;
        if (mm == null)
        {
            Debug.LogWarning("[ModCompatibility] Cannot export metadata: DFU ModManager is unavailable. Check that the DFU mod system is enabled.");
            return false; // do not write a misleading empty/valid snapshot
        }
        try
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("Server config directory is empty.");
            Directory.CreateDirectory(directory);
            var list = new List<InventoryEntry>();
            foreach (Mod mod in mm.GetAllMods())
            {
                if (mod == null || string.IsNullOrEmpty(mod.FileName)) continue;
                list.Add(new InventoryEntry {
                    fileName = Normalize(mod.FileName),
                    title = mod.ModInfo != null && !string.IsNullOrEmpty(mod.ModInfo.ModTitle) ? mod.ModInfo.ModTitle : mod.Title,
                    guid = ValidGuid(mod.GUID) ? Normalize(mod.GUID) : "",
                    version = mod.ModInfo == null ? "" : (mod.ModInfo.ModVersion ?? ""),
                    loadPriority = mod.LoadPriority,
                    enabled = mod.Enabled
                });
            }
            var snapshot = new InventorySnapshot {
                generatedUtc = DateTime.UtcNow.ToString("o"),
                mods = list.ToArray()
            };
            string target = Path.Combine(directory, "ServerModInventory.json");
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(snapshot, true));
            // Windows/.NET Framework File.Move does not overwrite existing files.
            // A failed replace should leave the prior snapshot intact when possible.
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            Debug.Log("[ModCompatibility] Exported " + list.Count + " DFU mod metadata entries (" +
                (mm.Initialized ? "initialized" : "pre-initialization") + ") to " + target);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ModCompatibility] Inventory export failed: " + e);
            return false;
        }
    }

    public static ModDescriptor[] InstalledEnabledMods()
    {
        var result = new List<ModDescriptor>();
        ModManager mm = ModManager.Instance;
        if (mm == null) return result.ToArray();
        foreach (Mod mod in mm.GetAllMods())
        {
            if (mod == null || !mod.Enabled) continue;
            result.Add(new ModDescriptor {
                fileName = Normalize(mod.FileName),
                guid = ValidGuid(mod.GUID) ? Normalize(mod.GUID) : "",
                version = mod.ModInfo == null ? "" : (mod.ModInfo.ModVersion ?? ""),
                loadPriority = mod.LoadPriority
            });
        }
        return result.ToArray();
    }

    public static ModCompatibilityPolicy LoadFromDirectory(string path)
    {
        string file = Path.Combine(path, "ServerMods.json");
        if (!File.Exists(file))
        {
            Debug.Log("[ModCompatibility] No ServerMods.json. Using relaxed/empty rules.");
            return new ModCompatibilityPolicy();
        }
        var p = JsonUtility.FromJson<ModCompatibilityPolicy>(File.ReadAllText(file));
        if (p == null) throw new FormatException("ServerMods.json must contain an object.");
        p.Validate();
        Debug.Log("[ModCompatibility] Loaded " + p.mode + " policy with " + p.rules.Length + " rules from " + file);
        return p;
    }

    public void Validate()
    {
        if (schemaVersion != 1 || (mode != "relaxed" && mode != "approved" && mode != "exact"))
            throw new FormatException("ServerMods.json has an unsupported schemaVersion or mode.");
        if (rules == null) rules = new ModRule[0];
        if (rules.Length > 4096) throw new FormatException("Too many mod rules.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule == null || (string.IsNullOrWhiteSpace(rule.fileName) && !ValidGuid(rule.guid)))
                throw new FormatException("Every mod rule needs a filename or GUID.");
            if (rule.status != "required" && rule.status != "allowed" && rule.status != "blocked" && rule.status != "unspecified")
                throw new FormatException("Unknown mod status: " + rule.status);
            if ((rule.fileName ?? "").Length > 256 || (rule.guid ?? "").Length > 128 || (rule.version ?? "").Length > 128)
                throw new FormatException("Mod rule identifier too long.");
            string key = ValidGuid(rule.guid) ? "guid:" + Normalize(rule.guid) : "file:" + Normalize(rule.fileName);
            if (!seen.Add(key)) throw new FormatException("Duplicate rule for " + key);
        }
    }

    public static bool SameMod(ModDescriptor a, ModDescriptor b)
    {
        if (ValidGuid(a.guid) && ValidGuid(b.guid)) return Normalize(a.guid) == Normalize(b.guid);
        return !string.IsNullOrEmpty(Normalize(a.fileName)) && Normalize(a.fileName) == Normalize(b.fileName);
    }

    static bool MatchRule(ModRule rule, ModDescriptor descriptor)
    {
        if (ValidGuid(rule.guid) && ValidGuid(descriptor.guid))
            return Normalize(rule.guid) == Normalize(descriptor.guid);
        return !string.IsNullOrEmpty(Normalize(rule.fileName)) && Normalize(rule.fileName) == Normalize(descriptor.fileName);
    }

    static string Label(ModDescriptor m) { return string.IsNullOrEmpty(m.fileName) ? m.guid : m.fileName; }

    // Returns a readable admission reason; null means accepted. Never runs client-side code.
    public string Check(ModDescriptor[] serverMods, ModDescriptor[] clientMods)
    {
        Validate();
        if (serverMods == null) serverMods = new ModDescriptor[0];
        if (clientMods == null) return "Client sent no mod manifest.";
        if (clientMods.Length > 1024) return "Too many mods in client manifest (limit 1024).";
        foreach (var mod in clientMods)
            if ((mod.fileName ?? "").Length > 256 || (mod.guid ?? "").Length > 128 ||
                (mod.version ?? "").Length > 128 ||
                (string.IsNullOrWhiteSpace(mod.fileName) && string.IsNullOrWhiteSpace(mod.guid)))
                return "Invalid mod entry in client manifest.";
        var errors = new List<string>();
        var required = new List<ModDescriptor>();
        if (mode == "exact")
        {
            required.AddRange(serverMods);
        }
        else
        {
            foreach (var rule in rules)
                if (rule.status == "required")
                {
                    var descriptor = new ModDescriptor { fileName = rule.fileName, guid = rule.guid, version = rule.version };
                    foreach (var m in serverMods) if (MatchRule(rule, m)) { descriptor = m; break; }
                    required.Add(descriptor);
                }
        }
        foreach (var req in required)
        {
            bool found = false;
            foreach (var cm in clientMods)
            {
                if (!SameMod(req, cm)) continue;
                found = true;
                if (matchVersions)
                {
                    if (string.IsNullOrWhiteSpace(req.version))
                        errors.Add("Cannot verify required mod version: " + Label(req) +
                            " (server metadata missing; disable version matching or provide its version)");
                    else if (string.IsNullOrWhiteSpace(cm.version))
                        errors.Add("Cannot verify required mod version: " + Label(req) + " (client metadata missing)");
                    else if (!string.Equals(req.version, cm.version, StringComparison.OrdinalIgnoreCase))
                        errors.Add("Wrong version: " + Label(req) + " (server " + req.version + ", client " + cm.version + ")");
                }
                break;
            }
            if (!found) errors.Add("Missing required: " + Label(req));
        }
        foreach (var cm in clientMods)
        {
            bool isRequired = false;
            foreach (var req in required) if (SameMod(req, cm)) { isRequired = true; break; }
            if (mode == "exact")
            {
                if (!isRequired) errors.Add("Not in server's exact mod set: " + Label(cm));
                continue;
            }
            string status = "unspecified";
            foreach (var rule in rules) if (MatchRule(rule, cm)) { status = rule.status; break; }
            if (status == "blocked") errors.Add("Blocked mod: " + Label(cm));
            else if (status == "unspecified" && mode == "approved") errors.Add("Not approved: " + Label(cm));
        }
        // Compare order of the required subset, not raw priority values or optional cosmetic mods.
        // O(serverMods * required + clientMods * required), not O(required^3).
        if (matchLoadOrder && required.Count > 1)
        {
            var serverOrdered = new List<ModDescriptor>();
            var clientOrdered = new List<ModDescriptor>();
            foreach (var sm in serverMods)
                foreach (var req in required) if (SameMod(sm, req)) { serverOrdered.Add(sm); break; }
            foreach (var cm in clientMods)
                foreach (var req in required) if (SameMod(cm, req)) { clientOrdered.Add(cm); break; }
            if (serverOrdered.Count == required.Count && clientOrdered.Count == required.Count)
            {
                serverOrdered.Sort((a, b) => a.loadPriority.CompareTo(b.loadPriority));
                clientOrdered.Sort((a, b) => a.loadPriority.CompareTo(b.loadPriority));
                for (int i = 0; i < required.Count; i++)
                    if (!SameMod(serverOrdered[i], clientOrdered[i]))
                    {
                        errors.Add("Required-mod load order differs from server.");
                        break;
                    }
            }
            // For required mods not enabled on the server, order is unknowable and not enforced.
        }
        if (errors.Count == 0) return null;
        int show = Math.Min(errors.Count, 12);
        var message = "Mod compatibility failed (" + errors.Count + " issue(s)):\n";
        for (int i = 0; i < show; i++) message += "- " + errors[i] + "\n";
        if (errors.Count > show) message += "...plus " + (errors.Count - show) + " more.\n";
        return message.TrimEnd();
    }
}
