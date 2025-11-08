using Microsoft.Win32;
using System.IO;
using System.Reflection;
using System;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Utilities
{
    public class AutostartManager
    {
        private const string RunKeyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string BackupKeyPath = "SOFTWARE\\GLM_Manager";
        private const string SteamValueName = "Steam";
        private const string GreenLumaValueName = "GreenLumaManager";
        private const string GreenLumaMonitorValueName = "GreenLumaMonitor";

        public static void ManageAutostart(bool replaceSteam, Config config)
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

                if (runKey == null)
                    return;

                if (replaceSteam && !string.IsNullOrWhiteSpace(config?.GreenLumaPath))
                {
                    ReplaceWithGreenLuma(runKey, config);
                }
                else
                {
                    RestoreOriginalSteam(runKey, config?.GreenLumaPath);
                }
            }
            catch
            {
            }
        }

        private static void ReplaceWithGreenLuma(RegistryKey runKey, Config config)
        {
            if (config == null)
                return;

            string? appPath = Environment.ProcessPath ??
                              Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);

            if (string.IsNullOrWhiteSpace(appPath))
                return;

            runKey.SetValue(GreenLumaMonitorValueName, $"\"{appPath}\" --launch-greenluma");
        }

        private static string CreateAutostartScript(string greenlumaPath, string injectorPath)
        {
            string vbsPath = Path.Combine(greenlumaPath, "GLM_Autostart.vbs");
            string noquestionPath = Path.Combine(greenlumaPath, "NoQuestion.bin");

            string escapedNoquestion = noquestionPath.Replace("\\", "\\\\");
            string escapedGlPath = greenlumaPath.Replace("\\", "\\\\");
            string escapedInjector = injectorPath.Replace("\\", "\\\\");

            string vbsContent = "Set fso = CreateObject(\"Scripting.FileSystemObject\")\n" +
                                "Set WshShell = CreateObject(\"WScript.Shell\")\n" +
                                "Set objWMIService = GetObject(\"winmgmts:\\\\localhost\\root\\cimv2\")\n\n" +
                                "Do While True\n" +
                                "    Set colProcesses = objWMIService.ExecQuery(\"SELECT * FROM Win32_Process WHERE Name = 'steam.exe'\")\n" +
                                "    If colProcesses.Count > 0 Then\n" +
                                "        Exit Do\n" +
                                "    End If\n" +
                                "    WScript.Sleep 500\n" +
                                "Loop\n\n" +
                                "WScript.Sleep 2000\n\n" +
                                "For Each objProcess in colProcesses\n" +
                                "    objProcess.Terminate()\n" +
                                "Next\n\n" +
                                $"fso.CreateTextFile(\"{escapedNoquestion}\").Close\n" +
                                $"WshShell.CurrentDirectory = \"{escapedGlPath}\"\n" +
                                $"WshShell.Run \"\"\"{escapedInjector}\"\"\", 0, False";

            File.WriteAllText(vbsPath, vbsContent);
            return vbsPath;
        }

        private static void RestoreOriginalSteam(RegistryKey runKey, string? greenlumaPath)
        {
            runKey.DeleteValue(GreenLumaMonitorValueName, throwOnMissingValue: false);
            CleanupVbsScript(greenlumaPath);
        }

        private static void CleanupVbsScript(string? greenlumaPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(greenlumaPath))
                {
                    string vbsPath = Path.Combine(greenlumaPath, "GLM_Autostart.vbs");
                    if (File.Exists(vbsPath))
                    {
                        File.Delete(vbsPath);
                    }
                }
            }
            catch
            {
            }
        }

        public static void SetAutostart(bool enable)
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

                if (runKey == null)
                    return;

                if (enable)
                {
                    string? appPath = Environment.ProcessPath ??
                                      Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);

                    if (!string.IsNullOrWhiteSpace(appPath))
                    {
                        runKey.SetValue(GreenLumaValueName, $"\"{appPath}\"");
                    }
                }
                else
                {
                    runKey.DeleteValue(GreenLumaValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
            }
        }

        public static bool IsAutostartEnabled()
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                string? value = runKey?.GetValue(GreenLumaValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        public static void CleanupAll()
        {
            try
            {
                RemoveGreenLumaAutostart();
                RemoveGreenLumaMonitor();
                DeleteBackupKey();
                CleanupAllVbsScripts();
            }
            catch
            {
            }
        }

        private static void CleanupAllVbsScripts()
        {
            try
            {
                string[] commonPaths =
                [
                    Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
                        "Steam"),
                    Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                        "Steam"),
                    "C:\\GreenLuma"
                ];

                foreach (string basePath in commonPaths)
                {
                    try
                    {
                        string vbsPath = Path.Combine(basePath, "GLM_Autostart.vbs");
                        if (File.Exists(vbsPath))
                        {
                            File.Delete(vbsPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void RemoveGreenLumaAutostart()
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                runKey?.DeleteValue(GreenLumaValueName, throwOnMissingValue: false);
            }
            catch
            {
            }
        }

        private static void RemoveGreenLumaMonitor()
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                runKey?.DeleteValue(GreenLumaMonitorValueName, throwOnMissingValue: false);
            }
            catch
            {
            }
        }

        private static void DeleteBackupKey()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(BackupKeyPath, throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }
    }
}
