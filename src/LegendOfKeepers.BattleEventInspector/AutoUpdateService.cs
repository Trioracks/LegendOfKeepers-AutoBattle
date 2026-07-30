using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LegendOfKeepers.BattleEventInspector;

// The plugin DLL is held open by the game process, so an update must be
// applied by a tiny external helper after Unity has exited.  This service
// deliberately trusts only the project's fixed GitHub release endpoint and
// verifies the SHA-256 published in its manifest before asking the helper to
// replace the one mod DLL.
internal static class AutoUpdateService
{
    private const string ManifestUrl = "https://github.com/Trioracks/LegendOfKeepers-AutoBattle/releases/latest/download/autobattle-update.json";
    private const string ReleaseBaseUrl = "https://github.com/Trioracks/LegendOfKeepers-AutoBattle/releases/download";
    private const string PluginFolderName = "LegendOfKeepers.BattleEventInspector";
    private const string PluginFileName = "LegendOfKeepers.BattleEventInspector.dll";
    private const string HelperFileName = "AutoBattle.Update.ps1";
    private const string ConfigFileName = "zubko.legendofkeepers.battleeventinspector.autoupdate.cfg";
    private const string StateFileName = "zubko.legendofkeepers.battleeventinspector.autoupdate-state.json";
    private const string PlanFileName = "zubko.legendofkeepers.battleeventinspector.autoupdate-plan.json";
    private static bool _checked;

    internal static bool TryScheduleUpdate(ManualLogSource log)
    {
        if (_checked) return false;
        _checked = true;

        try
        {
            var settings = LoadSettings();
            if (!settings.Enabled)
            {
                log.LogInfo("Auto-update is disabled by configuration.");
                return false;
            }

            var gameRoot = Path.GetFullPath(Paths.GameRootPath);
            var gameExecutable = Path.Combine(gameRoot, "LegendOfKeepers.exe");
            if (!File.Exists(gameExecutable))
            {
                log.LogWarning("Auto-update skipped: LegendOfKeepers.exe was not found under the BepInEx game root.");
                return false;
            }

            var pluginDirectory = Path.Combine(Paths.PluginPath, PluginFolderName);
            var pluginPath = Path.Combine(pluginDirectory, PluginFileName);
            if (!File.Exists(pluginPath))
            {
                log.LogWarning("Auto-update skipped: the installed mod DLL was not found in its expected plugin folder.");
                return false;
            }

            var manifest = DownloadManifest(settings.TimeoutSeconds);
            if (!TryValidateManifest(manifest, out var remoteVersion, out var validationIssue))
            {
                log.LogWarning($"Auto-update manifest ignored: {validationIssue}");
                return false;
            }

            var currentVersion = new Version(Plugin.PluginVersion);
            if (remoteVersion.CompareTo(currentVersion) <= 0)
            {
                log.LogInfo($"Auto-update: installed v{Plugin.PluginVersion} is current.");
                return false;
            }

            var statePath = Path.Combine(Paths.ConfigPath, StateFileName);
            var state = ReadJson<UpdateState>(statePath);
            if (string.Equals(state?.IgnoredVersion, manifest!.Version, StringComparison.Ordinal))
            {
                log.LogInfo($"Auto-update: v{manifest.Version} is suppressed after a user skip or a failed apply; the game will continue normally.");
                return false;
            }

            Directory.CreateDirectory(pluginDirectory);
            var helperPath = EnsureHelperScript(pluginDirectory);
            var planPath = Path.Combine(Paths.ConfigPath, PlanFileName);
            var plan = new UpdatePlan
            {
                ProcessId = Process.GetCurrentProcess().Id,
                GameRoot = gameRoot,
                GameExecutable = gameExecutable,
                PluginDirectory = pluginDirectory,
                PluginFileName = PluginFileName,
                TargetVersion = manifest.Version,
                PackageUrl = $"{ReleaseBaseUrl}/v{manifest.Version}/{manifest.Package}",
                PackageSha256 = manifest.Sha256,
                StatePath = statePath,
                LogPath = Path.Combine(Paths.BepInExRootPath, "LogOutput.AutoUpdate.log"),
            };
            WriteJsonAtomic(planPath, plan);

            if (!StartHelper(helperPath, planPath))
            {
                TryDelete(planPath);
                log.LogWarning("Auto-update helper could not be started; the game will continue with the installed version.");
                return false;
            }

            log.LogWarning($"Auto-update found v{manifest.Version}. A visible updater notice will explain the restart, then the helper will verify and install the signed-by-hash package.");
            Application.Quit();
            return true;
        }
        catch (Exception exception)
        {
            log.LogWarning($"Auto-update failed open: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static AutoUpdateSettings LoadSettings()
    {
        var path = Path.Combine(Paths.ConfigPath, ConfigFileName);
        var config = new ConfigFile(path, saveOnInit: true);
        var enabled = config.Bind("AutoUpdate", "Enabled", true, "Check the official GitHub release when the mod loads. An update is verified, applied outside the game process, then the game restarts automatically.").Value;
        var timeout = Math.Clamp(config.Bind("AutoUpdate", "ManifestTimeoutSeconds", 4, "Network timeout for the release manifest. When unavailable, the game starts normally without updating.").Value, 1, 15);
        return new AutoUpdateSettings(enabled, timeout);
    }

    private static UpdateManifest? DownloadManifest(int timeoutSeconds)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LegendOfKeepers-AutoBattle/{Plugin.PluginVersion}");
        var json = client.GetStringAsync(ManifestUrl).GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static bool TryValidateManifest(UpdateManifest? manifest, out Version version, out string issue)
    {
        version = new Version(0, 0);
        if (manifest is null) { issue = "empty response"; return false; }
        if (manifest.SchemaVersion != 1) { issue = $"unsupported schema {manifest.SchemaVersion}"; return false; }
        if (string.IsNullOrWhiteSpace(manifest.Version) || !Version.TryParse(manifest.Version, out var parsedVersion) || parsedVersion is null || parsedVersion.Build < 0)
        {
            issue = "version is not a three-part numeric release";
            return false;
        }
        version = parsedVersion;

        var expectedPackage = $"LegendOfKeepers_AutoBattle_Update_v{manifest.Version}.zip";
        if (!string.Equals(manifest.Package, expectedPackage, StringComparison.Ordinal))
        {
            issue = "package name does not match the fixed release layout";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256) || !Regex.IsMatch(manifest.Sha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
        {
            issue = "package SHA-256 is missing or malformed";
            return false;
        }

        issue = string.Empty;
        return true;
    }

    private static string EnsureHelperScript(string pluginDirectory)
    {
        var assembly = typeof(AutoUpdateService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(".assets.autobattle-update.ps1", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) throw new InvalidOperationException("Embedded auto-update helper resource is missing.");
        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Embedded auto-update helper stream is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var script = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("Embedded auto-update helper is empty.");

        var helperPath = Path.Combine(pluginDirectory, HelperFileName);
        if (File.Exists(helperPath) && string.Equals(File.ReadAllText(helperPath), script, StringComparison.Ordinal)) return helperPath;
        WriteTextAtomic(helperPath, script);
        return helperPath;
    }

    private static bool StartHelper(string helperPath, string planPath)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return false;
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? Paths.GameRootPath,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(helperPath);
        startInfo.ArgumentList.Add("-PlanPath");
        startInfo.ArgumentList.Add(planPath);
        return Process.Start(startInfo) is not null;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value) =>
        WriteTextAtomic(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteTextAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A parent directory is required."));
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* A stale plan is harmless and overwritten on the next update. */ }
    }

    private sealed record AutoUpdateSettings(bool Enabled, int TimeoutSeconds);
    private sealed class UpdateManifest
    {
        public int SchemaVersion { get; init; }
        public string Version { get; init; } = string.Empty;
        public string Package { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class UpdateState
    {
        public string? IgnoredVersion { get; init; }
    }

    private sealed class UpdatePlan
    {
        public int ProcessId { get; init; }
        public string GameRoot { get; init; } = string.Empty;
        public string GameExecutable { get; init; } = string.Empty;
        public string PluginDirectory { get; init; } = string.Empty;
        public string PluginFileName { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public string PackageUrl { get; init; } = string.Empty;
        public string PackageSha256 { get; init; } = string.Empty;
        public string StatePath { get; init; } = string.Empty;
        public string LogPath { get; init; } = string.Empty;
    }
}
