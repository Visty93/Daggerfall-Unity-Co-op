using System;

namespace Mirror
{
    // Lives in the Mirror assembly so transport startup can read the flag without
    // depending on Assembly-CSharp/DFU. Merely using -batchmode does not opt in.
    public sealed class DfuDedicatedServerOptions
    {
        public static readonly DfuDedicatedServerOptions Current = Parse(Environment.GetCommandLineArgs());
        public static bool Requested { get { return Current.IsRequested; } }

        public bool IsRequested { get; private set; }
        public string ConfigPath { get; private set; }
        public string DataPath { get; private set; }
        public int Port { get; private set; }
        public int MaxPlayers { get; private set; }
        public string Error { get; private set; }

        public static DfuDedicatedServerOptions Parse(string[] args)
        {
            var result = new DfuDedicatedServerOptions();
            if (args == null) return result;
            foreach (string arg in args)
                if (string.Equals(arg, "-dfu-server", StringComparison.OrdinalIgnoreCase))
                    result.IsRequested = true;
            if (!result.IsRequested) return result;

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                bool data = string.Equals(key, "-dfu-data", StringComparison.OrdinalIgnoreCase);
                bool port = string.Equals(key, "-dfu-port", StringComparison.OrdinalIgnoreCase);
                bool players = string.Equals(key, "-dfu-maxplayers", StringComparison.OrdinalIgnoreCase);
                bool config = string.Equals(key, "-dfu-config", StringComparison.OrdinalIgnoreCase);
                if (!data && !port && !players && !config) continue;
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("-", StringComparison.Ordinal))
                {
                    result.Error = "Missing value after " + key;
                    return result;
                }
                if (config)
                    result.ConfigPath = args[i];
                else if (data)
                    result.DataPath = args[i];
                else
                {
                    int value;
                    int maximum = port ? 65535 : 1024;
                    if (!int.TryParse(args[i], out value) || value < 1 || value > maximum)
                    {
                        result.Error = key + " must be between 1 and " + maximum + ".";
                        return result;
                    }
                    if (port) result.Port = value;
                    else result.MaxPlayers = value;
                }
            }
            return result;
        }
    }
}
