using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudRedirect.Resources;

namespace CloudRedirect.Services;

internal sealed record OpenSteamToolUpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    bool IsInstalled,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? AssetName,
    string? HtmlUrl,
    string? Error);

internal sealed record OpenSteamToolInstallResult(
    bool Success,
    string Version,
    IReadOnlyList<string> InstalledFiles,
    string? Error);

internal static class OpenSteamToolUpdater
{
    private const string VersionRecordFileName = "opensteamtool_version.json";
    private const string ReleasesApiUrl = "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest";
    private const string LatestReleaseUrl = "https://github.com/OpenSteam001/OpenSteamTool/releases/latest";
    private const string ReleaseDownloadBaseUrl = "https://github.com/OpenSteam001/OpenSteamTool/releases/download";
    private static readonly string[] RequiredFiles = ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static OpenSteamToolUpdater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudRedirect-OpenSteamTool-Updater");
    }

    public static string DetectInstalledVersion(string steamPath)
    {
        var dllPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (!File.Exists(dllPath))
            return S.Get("OpenSteamTool_NotInstalled");

        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            var version = FirstNonEmpty(info.ProductVersion, info.FileVersion);
            if (!string.IsNullOrWhiteSpace(version))
                return NormalizeVersionLabel(version);
        }
        catch
        {
            // Fall through to generic installed label.
        }

        var recordedVersion = TryReadRecordedVersion(steamPath);
        return !string.IsNullOrWhiteSpace(recordedVersion)
            ? recordedVersion
            : S.Get("OpenSteamTool_InstalledUnknownVersion");
    }

    public static async Task<OpenSteamToolUpdateInfo> CheckLatestAsync(string steamPath)
    {
        var current = DetectInstalledVersion(steamPath);
        var installed = OpenSteamToolIntegration.IsInstalled(steamPath);

        try
        {
            return await CheckLatestViaApiAsync(current, installed);
        }
        catch
        {
            try
            {
                return await CheckLatestViaReleasePageAsync(current, installed);
            }
            catch (Exception fallbackEx)
            {
                return new OpenSteamToolUpdateInfo(current, null, installed, false, null, null, null,
                    S.Format("OpenSteamTool_CheckFailedFriendly", fallbackEx.Message));
            }
        }
    }

    private static async Task<OpenSteamToolUpdateInfo> CheckLatestViaApiAsync(string current, bool installed)
    {
        var json = await Http.GetStringAsync(ReleasesApiUrl);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var latest = NormalizeVersionLabel(FirstNonEmpty(tag, name) ?? "");
        var html = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;

        string? downloadUrl = null;
        string? assetName = null;
        string? fallbackUrl = null;
        string? fallbackName = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var candidateName = asset.TryGetProperty("name", out var assetNameProp)
                    ? assetNameProp.GetString() ?? ""
                    : "";
                if (!candidateName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!candidateName.Contains("OpenSteamTool", StringComparison.OrdinalIgnoreCase))
                    continue;

                var candidateUrl = asset.GetProperty("browser_download_url").GetString();
                fallbackUrl ??= candidateUrl;
                fallbackName ??= candidateName;
                if (candidateName.Contains("Release", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = candidateUrl;
                    assetName = candidateName;
                    break;
                }
            }
        }

        downloadUrl ??= fallbackUrl;
        assetName ??= fallbackName;

        if (string.IsNullOrWhiteSpace(downloadUrl))
            return await CheckLatestViaReleasePageAsync(current, installed);

        var updateAvailable = !installed || IsNewerVersion(latest, current);
        return new OpenSteamToolUpdateInfo(current, latest, installed, updateAvailable,
            downloadUrl, assetName, html, null);
    }

    private static async Task<OpenSteamToolUpdateInfo> CheckLatestViaReleasePageAsync(string current, bool installed)
    {
        using var response = await Http.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? LatestReleaseUrl;
        var tag = ExtractTagFromReleaseUrl(finalUrl);
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException(S.Get("OpenSteamTool_LatestTagNotFound"));

        var latest = NormalizeVersionLabel(tag);
        var assetName = $"OpenSteamTool-{tag}-Release.zip";
        var downloadUrl = $"{ReleaseDownloadBaseUrl}/{tag}/{assetName}";
        var updateAvailable = !installed || IsNewerVersion(latest, current);

        return new OpenSteamToolUpdateInfo(current, latest, installed, updateAvailable,
            downloadUrl, assetName, finalUrl, null);
    }

    public static async Task<OpenSteamToolInstallResult> DownloadAndInstallAsync(
        string steamPath,
        string downloadUrl,
        string version,
        Action<string>? progress = null)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
        try
        {
            progress?.Invoke(S.Get("OpenSteamTool_Downloading"));
            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var dest = new FileStream(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(dest);
            }

            progress?.Invoke(S.Get("OpenSteamTool_Installing"));
            var installed = await Task.Run(() => ExtractRequiredFiles(tempZip, steamPath));
            await Task.Run(() => WriteVersionRecord(steamPath, version, downloadUrl));
            return new OpenSteamToolInstallResult(true, version, installed, null);
        }
        catch (Exception ex)
        {
            return new OpenSteamToolInstallResult(false, version, Array.Empty<string>(), ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);
            }
            catch
            {
                // Best effort cleanup of one explicit temp file.
            }
        }
    }

    private static IReadOnlyList<string> ExtractRequiredFiles(string zipPath, string steamPath)
    {
        var installed = new List<string>();
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var required in RequiredFiles)
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), required, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidOperationException(S.Format("OpenSteamTool_MissingFileInZip", required));

            var target = Path.Combine(steamPath, required);
            using var src = entry.Open();
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            FileUtils.AtomicWriteAllBytes(target, ms.ToArray());
            installed.Add(target);
        }

        return installed;
    }

    private static string GetVersionRecordPath(string steamPath)
    {
        return Path.Combine(steamPath, "cloud_redirect", VersionRecordFileName);
    }

    private static string? TryReadRecordedVersion(string steamPath)
    {
        try
        {
            var recordPath = GetVersionRecordPath(steamPath);
            var dllPath = Path.Combine(steamPath, "OpenSteamTool.dll");
            if (!File.Exists(recordPath) || !File.Exists(dllPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(recordPath));
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var versionProp)
                ? versionProp.GetString()
                : null;
            var recordedHash = root.TryGetProperty("sha256", out var hashProp)
                ? hashProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(recordedHash))
                return null;

            var currentHash = ComputeSha256(dllPath);
            return string.Equals(recordedHash, currentHash, StringComparison.OrdinalIgnoreCase)
                ? NormalizeVersionLabel(version)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteVersionRecord(string steamPath, string version, string downloadUrl)
    {
        var dllPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (!File.Exists(dllPath))
            return;

        var recordPath = GetVersionRecordPath(steamPath);
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);

        var payload = new
        {
            version = NormalizeVersionLabel(version),
            sha256 = ComputeSha256(dllPath),
            installed_at_utc = DateTimeOffset.UtcNow,
            source_url = downloadUrl,
            source_asset = Path.GetFileName(new Uri(downloadUrl).AbsolutePath)
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        FileUtils.AtomicWriteAllText(recordPath, json);
    }

    private static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    private static string NormalizeVersionLabel(string version)
    {
        version = version.Trim();
        if (version.StartsWith("OpenSteamTool ", StringComparison.OrdinalIgnoreCase))
            return version;
        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return "OpenSteamTool " + version;
        return string.IsNullOrWhiteSpace(version)
            ? S.Get("OpenSteamTool_InstalledUnknownVersion")
            : "OpenSteamTool v" + version.TrimStart('v', 'V');
    }

    private static string? ExtractTagFromReleaseUrl(string url)
    {
        var marker = "/releases/tag/";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var tag = url[(index + marker.Length)..];
        var slash = tag.IndexOfAny(new[] { '/', '?', '#' });
        if (slash >= 0) tag = tag[..slash];
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static bool IsNewerVersion(string? latest, string current)
    {
        if (!TryParseVersion(latest, out var latestVersion))
            return false;
        if (!TryParseVersion(current, out var currentVersion))
            return true;
        return latestVersion > currentVersion;
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Regex.Match(text, @"\d+(\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out version!);
    }
}
