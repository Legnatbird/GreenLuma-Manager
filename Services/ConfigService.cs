using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;
using Newtonsoft.Json.Linq;

namespace GreenLuma_Manager.Services;

public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLM_Manager");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");


    public static Config Load()
    {
        try
        {
            EnsureConfigDirectoryExists();

            if (!File.Exists(ConfigPath)) return CreateDefaultConfig();

            var configJson = File.ReadAllText(ConfigPath, Encoding.UTF8);

            var migratedConfig = TryMigrateFromRC3(configJson);
            if (migratedConfig != null) return migratedConfig;

            return DeserializeConfig(configJson) ?? new Config();
        }
        catch
        {
            return new Config();
        }
    }

    private static void EnsureConfigDirectoryExists()
    {
        if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
    }

    private static Config CreateDefaultConfig()
    {
        var config = new Config();
        var (steamPath, greenLumaPath) = PathDetector.DetectPaths();

        config.SteamPath = steamPath;
        config.GreenLumaPath = greenLumaPath;

        Save(config);
        return config;
    }

    private static Config? DeserializeConfig(string json)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var serializer = new DataContractJsonSerializer(typeof(Config));
            return (Config?)serializer.ReadObject(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Config? TryMigrateFromRC3(string configJson)
    {
        try
        {
            var jsonData = JObject.Parse(configJson);

            if (jsonData["steam_path"] == null && jsonData["SteamPath"] != null) return null;

            if (jsonData["steam_path"] != null)
            {
                var config = new Config
                {
                    SteamPath = jsonData["steam_path"]?.ToString() ?? string.Empty,
                    GreenLumaPath = jsonData["greenluma_path"]?.ToString() ?? string.Empty,
                    NoHook = jsonData["no_hook"]?.ToObject<bool>() ?? false,
                    DisableUpdateCheck = jsonData["disable_update_check"]?.ToObject<bool>() ?? false,
                    AutoUpdate = jsonData["auto_update"]?.ToObject<bool>() ?? true,
                    LastProfile = jsonData["last_profile"]?.ToString() ?? "default",
                    CheckUpdate = jsonData["check_update"]?.ToObject<bool>() ?? true,
                    ReplaceSteamAutostart = jsonData["replace_steam_autostart"]?.ToObject<bool>() ?? false,
                    FirstRun = false
                };

                SerializeConfig(config);
                return config;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(Config config)
    {
        try
        {
            EnsureConfigDirectoryExists();

            var json = SerializeConfig(config);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string SerializeConfig(Config config)
    {
        using var stream = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(Config));
        serializer.WriteObject(stream, config);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void WipeData()
    {
        try
        {
            AutostartManager.CleanupAll();

            if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, true);
        }
        catch
        {
        }
    }
}