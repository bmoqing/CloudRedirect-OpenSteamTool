using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using CloudRedirect.Resources;

namespace CloudRedirect.Services;

internal sealed record RestoreSourceInfo(
    string RootPath,
    string AccountId,
    string PlaytimeDir,
    string StatsDir,
    string? LuaArchivePath,
    string? LuaManifestPath,
    int PlaytimeCount,
    int StatsCount,
    int LuaCount);

internal sealed record RestoreSummary(
    string SourceRoot,
    string AccountId,
    int RestoredCount,
    int SkippedCount,
    string? BackupPath,
    string TargetPath,
    IReadOnlyList<string> Details);

internal static class DataRestoreService
{
    private const string BackupFolderName = "data_restore_backup";
    private const string LocalCacheRootName = "CloudRedirect";
    private const int MaxLuaFileBytes = 10 * 1024 * 1024;
    private const int MaxLuaArchiveBytes = 100 * 1024 * 1024;

    public static RestoreSourceInfo? FindBestSource(string steamPath, string? preferredAccountId = null)
    {
        var sources = FindSources(steamPath, preferredAccountId);
        return sources
            .OrderByDescending(s => string.Equals(s.AccountId, preferredAccountId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => s.PlaytimeCount + s.StatsCount + s.LuaCount)
            .ThenBy(s => SourceRank(steamPath, s.RootPath))
            .FirstOrDefault();
    }

    public static IReadOnlyList<RestoreSourceInfo> FindSources(string steamPath, string? preferredAccountId = null)
    {
        var roots = new List<string>();
        AddRoot(roots, Path.Combine(steamPath, "cloud_redirect", "storage"));
        AddRoot(roots, Path.Combine(steamPath, "cloud_redirect", "blobs"));

        var config = SteamDetector.ReadConfig();
        if (!string.IsNullOrWhiteSpace(config?.SyncPath))
            AddRoot(roots, config.SyncPath);

        AddRoot(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            LocalCacheRootName));
        AddRoot(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            LocalCacheRootName));
        AddRoot(roots, Path.Combine(@"D:\Downloads", LocalCacheRootName));

        var result = new List<RestoreSourceInfo>();
        foreach (var root in roots)
            AddSourcesFromRoot(result, root, preferredAccountId);

        return result
            .GroupBy(s => $"{Path.GetFullPath(s.RootPath).TrimEnd('\\')}|{s.AccountId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.PlaytimeCount + s.StatsCount + s.LuaCount).First())
            .ToList();
    }

    public static RestoreSummary RestorePlaytime(string steamPath, RestoreSourceInfo source)
    {
        var localConfigPath = Path.Combine(steamPath, "userdata", source.AccountId, "config", "localconfig.vdf");
        if (!Directory.Exists(source.PlaytimeDir))
            throw new InvalidOperationException(S.Format("DataRestore_SourceMissingFormat", source.PlaytimeDir));
        if (!File.Exists(localConfigPath))
            throw new InvalidOperationException(S.Format("DataRestore_TargetMissingFormat", localConfigPath));

        var files = Directory.GetFiles(source.PlaytimeDir, "*.bin", SearchOption.TopDirectoryOnly)
            .Where(f => TryParseAppId(Path.GetFileNameWithoutExtension(f), out _))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException(S.Get("DataRestore_NoPlaytimeBackups"));

        var backupPath = BackupFile(steamPath, localConfigPath);
        var lines = File.ReadAllLines(localConfigPath).ToList();
        var appsBlock = FindKeyBlock(lines, 0, lines.Count, "apps")
            ?? throw new InvalidOperationException(S.Get("DataRestore_LocalConfigAppsMissing"));

        var details = new List<string>();
        int restored = 0;
        int skipped = 0;

        foreach (var file in files)
        {
            var appId = Path.GetFileNameWithoutExtension(file);
            try
            {
                var playtime = ReadPlaytime(file);
                lines = UpsertAppPlaytime(lines, appsBlock.OpenBrace, appsBlock.CloseBrace,
                    appId, playtime.LastPlayed, playtime.Playtime, playtime.Playtime2wks);
                appsBlock = FindKeyBlock(lines, 0, lines.Count, "apps")!;
                restored++;
                details.Add(S.Format("DataRestore_PlaytimeDetailFormat", appId, playtime.Playtime));
            }
            catch (Exception ex)
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", appId, ex.Message));
            }
        }

        FileUtils.AtomicWriteAllLines(localConfigPath, lines);
        return new RestoreSummary(source.RootPath, source.AccountId, restored, skipped, backupPath,
            localConfigPath, details);
    }

    public static RestoreSummary RestoreStats(string steamPath, RestoreSourceInfo source)
    {
        if (!Directory.Exists(source.StatsDir))
            throw new InvalidOperationException(S.Format("DataRestore_SourceMissingFormat", source.StatsDir));

        var targetDir = Path.Combine(steamPath, "appcache", "stats");
        Directory.CreateDirectory(targetDir);

        var files = Directory.GetFiles(source.StatsDir, "*.bin", SearchOption.TopDirectoryOnly)
            .Where(f => TryParseAppId(Path.GetFileNameWithoutExtension(f), out _))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException(S.Get("DataRestore_NoStatsBackups"));

        var details = new List<string>();
        int restored = 0;
        int skipped = 0;
        string? firstBackup = null;

        foreach (var sourceFile in files)
        {
            var appId = Path.GetFileNameWithoutExtension(sourceFile);
            try
            {
                var target = Path.Combine(targetDir, $"UserGameStats_{source.AccountId}_{appId}.bin");
                if (File.Exists(target))
                {
                    var backupPath = BackupFile(steamPath, target);
                    firstBackup ??= backupPath;
                }

                FileUtils.AtomicCopy(sourceFile, target);
                restored++;
                details.Add(S.Format("DataRestore_StatsDetailFormat", appId));
            }
            catch (Exception ex)
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", appId, ex.Message));
            }
        }

        return new RestoreSummary(source.RootPath, source.AccountId, restored, skipped, firstBackup,
            targetDir, details);
    }

    public static RestoreSummary RestoreLua(string steamPath, RestoreSourceInfo source)
    {
        if (source.LuaArchivePath == null || !File.Exists(source.LuaArchivePath))
            throw new InvalidOperationException(S.Get("DataRestore_NoLuaBackups"));

        var targetDir = OpenSteamToolIntegration.GetDefaultLuaDir(steamPath);
        Directory.CreateDirectory(targetDir);
        var targetRoot = EnsureTrailingSeparator(Path.GetFullPath(targetDir));
        var backupRoot = Path.Combine(steamPath, "cloud_redirect", BackupFolderName,
            DateTime.Now.ToString("yyyyMMdd-HHmmss"), "lua");

        var details = new List<string>();
        int restored = 0;
        int skipped = 0;
        var knownAlive = ReadAliveLuaManifest(source.LuaManifestPath);

        using var archiveStream = File.OpenRead(source.LuaArchivePath);
        if (archiveStream.Length > MaxLuaArchiveBytes)
            throw new InvalidOperationException(S.Get("DataRestore_LuaArchiveTooLarge"));

        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = entry.FullName.Replace('\\', '/');
            if (fileName.Contains('/'))
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", entry.FullName,
                    S.Get("DataRestore_InvalidLuaName")));
                continue;
            }

            if (!IsValidLuaFilename(fileName) ||
                (knownAlive.Count > 0 && !knownAlive.Contains(fileName)))
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", entry.FullName,
                    S.Get("DataRestore_InvalidLuaName")));
                continue;
            }

            if (entry.Length <= 0 || entry.Length > MaxLuaFileBytes)
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", entry.FullName,
                    S.Get("DataRestore_InvalidLuaSize")));
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(targetDir, fileName));
            if (!target.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", entry.FullName,
                    S.Get("DataRestore_InvalidLuaName")));
                continue;
            }

            try
            {
                using var ms = new MemoryStream();
                using (var entryStream = entry.Open())
                    entryStream.CopyTo(ms);
                var data = ms.ToArray();
                if (data.AsSpan(0, Math.Min(data.Length, 8192)).IndexOf((byte)0) >= 0)
                {
                    skipped++;
                    details.Add(S.Format("DataRestore_SkippedDetailFormat", entry.FullName,
                        S.Get("DataRestore_InvalidLuaContent")));
                    continue;
                }

                if (File.Exists(target))
                {
                    Directory.CreateDirectory(backupRoot);
                    FileUtils.AtomicCopy(target, Path.Combine(backupRoot, fileName));
                }

                FileUtils.AtomicWriteAllBytes(target, data);
                restored++;
                details.Add(S.Format("DataRestore_LuaDetailFormat", fileName));
            }
            catch (Exception ex)
            {
                skipped++;
                details.Add(S.Format("DataRestore_SkippedDetailFormat", entry.FullName, ex.Message));
            }
        }

        if (restored == 0 && skipped == 0)
            throw new InvalidOperationException(S.Get("DataRestore_NoLuaBackups"));

        return new RestoreSummary(source.RootPath, source.AccountId, restored, skipped,
            Directory.Exists(backupRoot) ? backupRoot : null, targetDir, details);
    }

    private static void AddSourcesFromRoot(List<RestoreSourceInfo> result, string root, string? preferredAccountId)
    {
        if (!Directory.Exists(root)) return;

        IEnumerable<string> accountDirs;
        try { accountDirs = Directory.GetDirectories(root); }
        catch { return; }

        foreach (var accountDir in accountDirs)
        {
            var accountId = Path.GetFileName(accountDir);
            if (!IsNumeric(accountId)) continue;
            if (!string.IsNullOrWhiteSpace(preferredAccountId) &&
                !string.Equals(accountId, preferredAccountId, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var layout in GetAccountLayouts(accountDir))
            {
                var playtimeDir = Path.Combine(layout, "Playtime");
                var statsDir = Path.Combine(layout, "UserGameStats");
                var luaArchivePath = Path.Combine(layout, "LuaArchive.zip");
                var luaManifestPath = Path.Combine(layout, "LuaManifest.json");

                var playtimeCount = CountAppBins(playtimeDir);
                var statsCount = CountAppBins(statsDir);
                var luaCount = CountLuaArchiveEntries(luaArchivePath, luaManifestPath);
                if (playtimeCount == 0 && statsCount == 0 && luaCount == 0)
                    continue;

                result.Add(new RestoreSourceInfo(
                    root,
                    accountId,
                    playtimeDir,
                    statsDir,
                    File.Exists(luaArchivePath) ? luaArchivePath : null,
                    File.Exists(luaManifestPath) ? luaManifestPath : null,
                    playtimeCount,
                    statsCount,
                    luaCount));
            }
        }
    }

    private static IEnumerable<string> GetAccountLayouts(string accountDir)
    {
        yield return Path.Combine(accountDir, "0");
        yield return Path.Combine(accountDir, "0", "blobs");
    }

    private static int CountAppBins(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.GetFiles(dir, "*.bin", SearchOption.TopDirectoryOnly)
                .Count(f => TryParseAppId(Path.GetFileNameWithoutExtension(f), out _));
        }
        catch { return 0; }
    }

    private static int CountLuaArchiveEntries(string archivePath, string manifestPath)
    {
        if (!File.Exists(archivePath)) return 0;
        var alive = ReadAliveLuaManifest(File.Exists(manifestPath) ? manifestPath : null);
        try
        {
            using var stream = File.OpenRead(archivePath);
            if (stream.Length > MaxLuaArchiveBytes) return 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return zip.Entries.Count(e =>
            {
                var name = e.FullName.Replace('\\', '/');
                return !name.Contains('/') &&
                       IsValidLuaFilename(name) &&
                       e.Length > 0 &&
                       e.Length <= MaxLuaFileBytes &&
                       (alive.Count == 0 || alive.Contains(name));
            });
        }
        catch { return 0; }
    }

    private static HashSet<string> ReadAliveLuaManifest(string? path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!IsValidLuaFilename(prop.Name)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var mod = ReadJsonUInt64(prop.Value, "mod");
                    var del = ReadJsonUInt64(prop.Value, "del");
                    if (del > mod) continue;
                }
                result.Add(prop.Name);
            }
        }
        catch { }

        return result;
    }

    private static ulong ReadJsonUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt64(out var n))
            return n;
        if (prop.ValueKind == JsonValueKind.String && ulong.TryParse(prop.GetString(), out n))
            return n;
        return 0;
    }

    private static PlaytimeRecord ReadPlaytime(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new PlaytimeRecord(
            ReadRequiredStringOrNumber(root, "LastPlayed"),
            ReadRequiredStringOrNumber(root, "Playtime"),
            ReadRequiredStringOrNumber(root, "Playtime2wks"));
    }

    private static string ReadRequiredStringOrNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
            return "0";
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetUInt64(out var value) => value.ToString(),
            JsonValueKind.String => prop.GetString() ?? "0",
            _ => "0",
        };
    }

    private static List<string> UpsertAppPlaytime(
        List<string> lines,
        int appsOpenBrace,
        int appsCloseBrace,
        string appId,
        string lastPlayed,
        string playtime,
        string playtime2wks)
    {
        var appBlock = FindKeyBlock(lines, appsOpenBrace + 1, appsCloseBrace, appId);
        if (appBlock == null)
        {
            const string appIndent = "\t\t\t\t\t";
            const string valueIndent = "\t\t\t\t\t\t";
            var newBlock = new[]
            {
                $"{appIndent}\"{appId}\"",
                $"{appIndent}{{",
                $"{valueIndent}\"LastPlayed\"\t\t\"{lastPlayed}\"",
                $"{valueIndent}\"Playtime2wks\"\t\t\"{playtime2wks}\"",
                $"{valueIndent}\"Playtime\"\t\t\"{playtime}\"",
                $"{appIndent}}}",
            };
            lines.InsertRange(appsCloseBrace, newBlock);
            return lines;
        }

        var indent = GetValueIndent(lines, appBlock.OpenBrace);
        lines = SetImmediateValue(lines, appBlock.OpenBrace, appBlock.CloseBrace,
            "LastPlayed", lastPlayed, indent);
        appBlock = FindKeyBlock(lines, appsOpenBrace + 1, GetBlockEnd(lines, appsOpenBrace), appId)!;
        lines = SetImmediateValue(lines, appBlock.OpenBrace, appBlock.CloseBrace,
            "Playtime2wks", playtime2wks, indent);
        appBlock = FindKeyBlock(lines, appsOpenBrace + 1, GetBlockEnd(lines, appsOpenBrace), appId)!;
        lines = SetImmediateValue(lines, appBlock.OpenBrace, appBlock.CloseBrace,
            "Playtime", playtime, indent);
        return lines;
    }

    private static List<string> SetImmediateValue(
        List<string> lines,
        int blockOpenBrace,
        int blockCloseBrace,
        string key,
        string value,
        string indent)
    {
        var depth = 0;
        for (var i = blockOpenBrace + 1; i < blockCloseBrace; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "{")
            {
                depth++;
                continue;
            }
            if (trimmed == "}")
            {
                depth--;
                continue;
            }
            if (depth == 0 && TryParseVdfKey(lines[i], out var existing, out _) &&
                string.Equals(existing, key, StringComparison.Ordinal))
            {
                lines[i] = $"{indent}\"{key}\"\t\t\"{value}\"";
                return lines;
            }
        }

        lines.Insert(blockCloseBrace, $"{indent}\"{key}\"\t\t\"{value}\"");
        return lines;
    }

    private static KeyBlock? FindKeyBlock(List<string> lines, int searchStart, int searchEnd, string key)
    {
        searchEnd = Math.Min(searchEnd, lines.Count);
        for (var i = searchStart; i < searchEnd; i++)
        {
            if (!TryParseVdfKey(lines[i].Trim(), out var currentKey, out _) ||
                !string.Equals(currentKey, key, StringComparison.Ordinal))
                continue;

            for (var j = i + 1; j < searchEnd; j++)
            {
                var trimmed = lines[j].Trim();
                if (trimmed == "{")
                    return new KeyBlock(i, j, GetBlockEnd(lines, j));
                if (trimmed.Length > 0)
                    break;
            }
        }

        return null;
    }

    private static int GetBlockEnd(List<string> lines, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "{")
            {
                depth++;
            }
            else if (trimmed == "}")
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        throw new InvalidOperationException(S.Get("DataRestore_VdfBlockBroken"));
    }

    private static string GetValueIndent(List<string> lines, int openBrace)
    {
        if (openBrace + 1 < lines.Count)
        {
            var line = lines[openBrace + 1];
            var len = line.TakeWhile(char.IsWhiteSpace).Count();
            if (len > 0) return line[..len];
        }
        return "\t\t\t\t\t\t";
    }

    private static bool TryParseVdfKey(string line, out string key, out int endIndex)
    {
        key = "";
        endIndex = 0;
        if (line.Length < 2 || line[0] != '"') return false;
        var end = line.IndexOf('"', 1);
        if (end < 0) return false;
        key = line[1..end];
        endIndex = end + 1;
        return true;
    }

    private static string BackupFile(string steamPath, string filePath)
    {
        var relative = Path.GetRelativePath(steamPath, filePath);
        var backup = Path.Combine(steamPath, "cloud_redirect", BackupFolderName,
            DateTime.Now.ToString("yyyyMMdd-HHmmss"), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        FileUtils.AtomicCopy(filePath, backup);
        return backup;
    }

    private static bool IsValidLuaFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
            name.Contains(':') || name.Contains('\n') || name.Contains('\r'))
            return false;

        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.');
        if (stem.Length == 0) return false;
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];
        return !reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseAppId(string text, out uint appId)
        => uint.TryParse(text, out appId) && appId > 0;

    private static bool IsNumeric(string value)
        => value.Length > 0 && value.All(char.IsDigit);

    private static void AddRoot(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) return;
            if (roots.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(path);
        }
        catch { }
    }

    private static int SourceRank(string steamPath, string rootPath)
    {
        var steamStorage = Path.Combine(steamPath, "cloud_redirect", "storage");
        return string.Equals(Path.GetFullPath(rootPath), Path.GetFullPath(steamStorage), StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
            path += Path.DirectorySeparatorChar;
        return path;
    }

    private sealed record PlaytimeRecord(string LastPlayed, string Playtime, string Playtime2wks);
    private sealed record KeyBlock(int KeyLine, int OpenBrace, int CloseBrace);
}
