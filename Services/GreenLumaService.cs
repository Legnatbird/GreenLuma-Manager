using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

public partial class GreenLumaService
{
    private const int SteamKillDelayMs = 2000;
    private const int ProcessKillTimeoutMs = 5000;
    private static readonly string[] SteamProcessNames = ["steam", "steamwebhelper", "steamerrorfilereporter"];

    [GeneratedRegex(@"[A-Za-z]:\\[^""\r\n]+?\.dll", RegexOptions.IgnoreCase)]
    private static partial Regex DllPathRegex();

    public static bool IsAppListGenerated(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.GreenLumaPath))
            return false;

        var appListPath = Path.Combine(config.GreenLumaPath, "AppList");

        return Directory.Exists(appListPath) &&
               Directory.GetFiles(appListPath, "*.txt").Length > 0;
    }

    public static async Task<bool> GenerateAppListAsync(Profile profile, Config config)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!ValidateGreenLumaPath(config.GreenLumaPath))
                    return false;

                var appListPath = Path.Combine(config.GreenLumaPath, "AppList");

                RecreateAppListDirectory(appListPath);
                WriteAppListFiles(profile, appListPath);
                UpdateInjectorIni(config);

                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool ValidateGreenLumaPath(string greenLumaPath)
    {
        if (string.IsNullOrWhiteSpace(greenLumaPath))
            return false;

        return Directory.Exists(greenLumaPath);
    }

    private static void RecreateAppListDirectory(string appListPath)
    {
        if (Directory.Exists(appListPath)) Directory.Delete(appListPath, true);

        Directory.CreateDirectory(appListPath);
    }

    private static void WriteAppListFiles(Profile profile, string appListPath)
    {
        var appIds = profile.Games
            .Select(g => g.AppId)
            .Distinct()
            .ToList();

        for (var i = 0; i < appIds.Count; i++)
        {
            var filePath = Path.Combine(appListPath, $"{i}.txt");
            File.WriteAllText(filePath, appIds[i]);
        }
    }

    public static async Task<bool> LaunchGreenLumaAsync(Config config)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!ValidatePaths(config))
                    return false;

                KillSteam();

                return LaunchInjector(config);
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool ValidatePaths(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.SteamPath) ||
            string.IsNullOrWhiteSpace(config.GreenLumaPath))
            return false;

        var steamExePath = Path.Combine(config.SteamPath, "Steam.exe");
        var injectorPath = Path.Combine(config.GreenLumaPath, "DLLInjector.exe");

        return File.Exists(steamExePath) && File.Exists(injectorPath);
    }

    private static bool LaunchInjector(Config config)
    {
        var injectorPath = Path.Combine(config.GreenLumaPath, "DLLInjector.exe");

        if (!File.Exists(injectorPath))
            return false;

        UpdateInjectorIni(config);

        Process.Start(new ProcessStartInfo
        {
            FileName = injectorPath,
            WorkingDirectory = config.GreenLumaPath,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        return true;
    }

    private static void KillSteam()
    {
        try
        {
            var steamExePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam",
                "Steam.exe");

            if (File.Exists(steamExePath))
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = steamExePath,
                        Arguments = "-shutdown",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Thread.Sleep(2000);
                }
                catch
                {
                }

            foreach (var processName in SteamProcessNames) KillProcessesByName(processName);
        }
        catch
        {
        }
    }

    private static void KillProcessesByName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
            try
            {
                process.Kill();
                process.WaitForExit(ProcessKillTimeoutMs);
            }
            catch
            {
            }
    }

    private static bool AreSameDirectory(string path1, string path2)
    {
        try
        {
            var fullPath1 = Path.GetFullPath(path1)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath2 = Path.GetFullPath(path2)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath1, fullPath2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateInjectorIni(Config config)
    {
        try
        {
            var iniPath = Path.Combine(config.GreenLumaPath, "DLLInjector.ini");

            if (!File.Exists(iniPath))
                return;

            var lines = File.ReadAllLines(iniPath).ToList();
            var dllValue = ExtractDllValue(lines);
            var settings = BuildInjectorSettings(config, dllValue);
            var updatedLines = ApplySettings(lines, settings);

            File.WriteAllLines(iniPath, updatedLines);
        }
        catch
        {
        }
    }

    private static string? ExtractDllValue(List<string> lines)
    {
        try
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("Dll", StringComparison.OrdinalIgnoreCase))
                {
                    var equalsIndex = line.IndexOf('=');
                    if (equalsIndex >= 0 && equalsIndex < line.Length - 1)
                    {
                        var raw = line[(equalsIndex + 1)..].Trim();
                        var cleaned = CleanDllValue(raw);
                        return cleaned;
                    }

                    break;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string CleanDllValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();
        s = s.Trim('"', '\'', ' ');

        try
        {
            var m = DllPathRegex().Match(s);

            if (m.Success) return m.Value;
        }
        catch
        {
        }

        return s;
    }

    private static Dictionary<string, string> BuildInjectorSettings(Config config, string? dllValue)
    {
        var useSeparatePaths = !AreSameDirectory(config.SteamPath, config.GreenLumaPath) ||
                               (!string.IsNullOrWhiteSpace(dllValue) && Path.IsPathRooted(dllValue));

        var steamExePath = Path.Combine(config.SteamPath, "Steam.exe");

        var settings = new Dictionary<string, string>
        {
            ["FileToCreate_1"] = " NoQuestion.bin"
        };

        if (useSeparatePaths)
        {
            settings["UseFullPathsFromIni"] = " 1";
            settings["Exe"] = $" \"{steamExePath}\"";

            if (!string.IsNullOrWhiteSpace(dllValue))
            {
                var candidate = dllValue.Trim();

                bool rooted;
                try
                {
                    rooted = Path.IsPathRooted(candidate);
                }
                catch
                {
                    rooted = false;
                }

                if (rooted)
                {
                    var full = candidate;
                    try
                    {
                        full = Path.GetFullPath(candidate);
                    }
                    catch
                    {
                    }

                    settings["Dll"] = $" \"{full}\"";
                }
                else
                {
                    var fullDllPath = Path.Combine(config.GreenLumaPath, candidate);
                    try
                    {
                        fullDllPath = Path.GetFullPath(fullDllPath);
                    }
                    catch
                    {
                    }

                    settings["Dll"] = $" \"{fullDllPath}\"";
                }
            }
        }
        else
        {
            settings["UseFullPathsFromIni"] = " 0";
            settings["Exe"] = " Steam.exe";

            if (!string.IsNullOrWhiteSpace(dllValue)) settings["Dll"] = $" {dllValue}";
        }

        if (config.NoHook)
            ApplyStealthModeSettings(settings);
        else
            ApplyNormalModeSettings(settings);

        return settings;
    }

    private static void ApplyStealthModeSettings(Dictionary<string, string> settings)
    {
        settings["CommandLine"] = "";
        settings["WaitForProcessTermination"] = " 0";
        settings["EnableFakeParentProcess"] = " 1";
        settings["EnableMitigationsOnChildProcess"] = " 0";
        settings["CreateFiles"] = " 2";
        settings["FileToCreate_2"] = " StealthMode.bin";
    }

    private static void ApplyNormalModeSettings(Dictionary<string, string> settings)
    {
        settings["CommandLine"] = " -inhibitbootstrap";
        settings["WaitForProcessTermination"] = " 1";
        settings["EnableFakeParentProcess"] = " 0";
        settings["CreateFiles"] = " 1";
        if (!settings.ContainsKey("FileToCreate_2")) settings["FileToCreate_2"] = "";
    }

    private static List<string> ApplySettings(List<string> originalLines, Dictionary<string, string> settings)
    {
        var result = new List<string>();

        foreach (var line in originalLines)
        {
            var trimmed = line.Trim();
            var matched = false;

            if (!string.IsNullOrWhiteSpace(trimmed) && trimmed[0] != '#' && trimmed.Contains('='))
            {
                var equalsIndex = trimmed.IndexOf('=');
                var key = trimmed[..equalsIndex].Trim();

                foreach (var setting in settings)
                    if (string.Equals(key, setting.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add($"{setting.Key}={setting.Value}");
                        matched = true;
                        break;
                    }
            }

            if (!matched) result.Add(line);
        }

        return result;
    }
}