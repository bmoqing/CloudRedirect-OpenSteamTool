using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CloudRedirect.Resources;

namespace CloudRedirect.Services;

internal sealed record DiagnosticItem(string Name, bool Ok, string Detail);

internal sealed record DiagnosticsSnapshot(
    string? SteamPath,
    string? LogPath,
    long? SteamVersion,
    bool SteamVersionSupported,
    DllIdentityInfo DllIdentity,
    OpenSteamToolCheckResult? OpenSteamTool,
    IReadOnlyList<DiagnosticItem> Items,
    IReadOnlyList<string> RecentLogLines);

internal static class DiagnosticsService
{
    public static DiagnosticsSnapshot Load()
    {
        var steamPath = SteamDetector.FindSteamPath();
        var logPath = SteamDetector.GetLogPath();
        var version = steamPath != null ? SteamDetector.GetSteamVersion(steamPath) : null;
        var dll = DllIdentity.GetCurrent();
        OpenSteamToolCheckResult? ost = null;
        if (steamPath != null)
            ost = OpenSteamToolIntegration.CheckInstallation(steamPath);

        var recentLog = logPath != null && File.Exists(logPath)
            ? ReadTail(logPath, 260)
            : Array.Empty<string>();

        var items = new List<DiagnosticItem>();
        Add(items, steamPath != null, S.Get("Diagnostics_SteamPath"), steamPath ?? S.Get("Diagnostics_SteamPathNotFound"));
        Add(items, version != null && SteamDetector.IsSupportedSteamVersion(version.Value),
            S.Get("Diagnostics_SteamVersion"),
            version != null
                ? SteamDetector.IsSupportedSteamVersion(version.Value)
                    ? S.Format("Diagnostics_SteamVersionSupported", version.Value)
                    : S.Format("Diagnostics_SteamVersionUnsupported", version.Value)
                : S.Get("Diagnostics_SteamVersionUnknown"));
        Add(items, dll.Kind is DllSourceKind.CustomWebDav or DllSourceKind.Official,
            S.Get("Diagnostics_DllSource"), S.Format("Diagnostics_DllSourceFormat", dll.DisplayText, dll.DetailText));

        if (ost != null)
        {
            Add(items, ost.IsReady, S.Get("Diagnostics_OpenSteamToolIntegration"),
                ost.IsReady ? S.Get("Diagnostics_OpenSteamToolReady") : S.Get("Diagnostics_OpenSteamToolFailed"));
            Add(items, ost.CurrentUser != null, S.Get("Diagnostics_CurrentAccount"),
                ost.CurrentUser != null
                    ? S.Format("Diagnostics_CurrentAccountFormat",
                        string.IsNullOrWhiteSpace(ost.CurrentUser.PersonaName) ? ost.CurrentUser.AccountName : ost.CurrentUser.PersonaName,
                        ost.CurrentUser.AccountId,
                        ost.CurrentUser.SteamId64)
                    : S.Get("Diagnostics_CurrentAccountMissing"));
        }

        var loaded = recentLog.Any(l => l.Contains("CloudRedirect loaded via OpenSteamTool Lua", StringComparison.OrdinalIgnoreCase));
        var hookActive = recentLog.Any(l => l.Contains("All hooks ACTIVE", StringComparison.OrdinalIgnoreCase)) ||
                         recentLog.Any(l => l.Contains("vtable=1", StringComparison.OrdinalIgnoreCase));
        var accountOk = recentLog.Any(l => l.Contains("[Account] Captured SteamID64", StringComparison.OrdinalIgnoreCase)) ||
                        recentLog.Any(l => l.Contains("[HTTP] Account ID set to", StringComparison.OrdinalIgnoreCase));
        var noAccount = recentLog.Any(l => l.Contains("no Steam account ID", StringComparison.OrdinalIgnoreCase));
        var webDav = recentLog.Any(l => l.Contains("[WebDAV] Initialized", StringComparison.OrdinalIgnoreCase));
        var intercept = recentLog.LastOrDefault(l => l.Contains("INTERCEPT Cloud.", StringComparison.OrdinalIgnoreCase));

        Add(items, loaded, S.Get("Diagnostics_DllLoaded"),
            loaded ? S.Get("Diagnostics_DllLoadedOk") : S.Get("Diagnostics_DllLoadedMissing"));
        Add(items, hookActive, S.Get("Diagnostics_CloudRpcHook"),
            hookActive ? S.Get("Diagnostics_CloudRpcHookOk") : S.Get("Diagnostics_CloudRpcHookMissing"));
        Add(items, accountOk && !noAccount, S.Get("Diagnostics_AccountId"),
            noAccount
                ? S.Get("Diagnostics_AccountIdRecentMissing")
                : accountOk ? S.Get("Diagnostics_AccountIdOk") : S.Get("Diagnostics_AccountIdMissing"));
        Add(items, webDav, S.Get("Diagnostics_WebDavProvider"),
            webDav ? S.Get("Diagnostics_WebDavProviderOk") : S.Get("Diagnostics_WebDavProviderMissing"));
        Add(items, intercept != null, S.Get("Diagnostics_RecentSyncActivity"),
            intercept ?? S.Get("Diagnostics_RecentSyncActivityMissing"));

        return new DiagnosticsSnapshot(
            steamPath,
            logPath,
            version,
            version != null && SteamDetector.IsSupportedSteamVersion(version.Value),
            dll,
            ost,
            items,
            recentLog);
    }

    private static void Add(List<DiagnosticItem> items, bool ok, string name, string detail)
    {
        items.Add(new DiagnosticItem(name, ok, detail));
    }

    private static string[] ReadTail(string path, int maxLines)
    {
        try
        {
            var queue = new Queue<string>(maxLines);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
            {
                if (queue.Count == maxLines)
                    queue.Dequeue();
                queue.Enqueue(line);
            }
            return queue.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
