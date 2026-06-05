using System;
using System.IO;
using System.Text;

namespace CloudRedirect.Services;

internal enum DllSourceKind
{
    NotInstalled,
    Official,
    CustomWebDav,
    HashMismatch,
    Unknown,
}

internal sealed record DllIdentityInfo(
    DllSourceKind Kind,
    string DisplayText,
    string DetailText,
    string? DeployedHash,
    string? EmbeddedHash,
    bool IsCustomBuild);

internal static class DllIdentity
{
    private const string CustomWebDavHash =
        "1B59851D0C0674F1A87DC369F757A75A8B0F27C5A1E4CA696EC9439EDC0C0778";
    private const string EmbeddedDllResourceName = "cloud_redirect.dll";
    private static readonly byte[][] CustomBuildMarkers =
    [
        Encoding.ASCII.GetBytes("Custom OpenSteamTool/WebDAV build active"),
        Encoding.ASCII.GetBytes("custom WebDAV build"),
    ];

    public static DllIdentityInfo GetCurrent()
    {
        var steamPath = SteamDetector.FindSteamPath();
        var embeddedHash = EmbeddedDll.GetEmbeddedHash();
        var embeddedIsCustom = IsCustomHash(embeddedHash) || EmbeddedDllContainsCustomMarker();

        if (steamPath == null)
        {
            return new DllIdentityInfo(
                DllSourceKind.Unknown,
                S("Settings_DllSourceUnknown"),
                S("Settings_DllSourceSteamMissing"),
                null,
                embeddedHash,
                embeddedIsCustom);
        }

        var dllPath = Path.Combine(steamPath, "cloud_redirect.dll");
        if (!File.Exists(dllPath))
        {
            return new DllIdentityInfo(
                DllSourceKind.NotInstalled,
                S("Settings_DllSourceNotInstalled"),
                S("Settings_DllSourceNotInstalledHint"),
                null,
                embeddedHash,
                embeddedIsCustom);
        }

        string? deployedHash;
        bool deployedHasCustomMarker;
        try
        {
            deployedHash = ComputeSha256(dllPath);
            deployedHasCustomMarker = FileContainsCustomMarker(dllPath);
        }
        catch
        {
            return new DllIdentityInfo(
                DllSourceKind.Unknown,
                S("Settings_DllSourceUnknown"),
                S("Settings_DllSourceReadFailed"),
                null,
                embeddedHash,
                embeddedIsCustom);
        }

        var deployedIsCustom = IsCustomHash(deployedHash) || deployedHasCustomMarker;
        var deployedMatchesEmbedded = embeddedHash != null &&
            string.Equals(deployedHash, embeddedHash, StringComparison.OrdinalIgnoreCase);

        if (deployedIsCustom)
        {
            return new DllIdentityInfo(
                DllSourceKind.CustomWebDav,
                S("Settings_DllSourceCustomWebDav"),
                deployedMatchesEmbedded
                    ? S("Settings_DllSourceMatchesEmbedded")
                    : S("Settings_DllSourceCustomDifferentEmbedded"),
                deployedHash,
                embeddedHash,
                true);
        }

        if (deployedMatchesEmbedded && !embeddedIsCustom)
        {
            return new DllIdentityInfo(
                DllSourceKind.Official,
                S("Settings_DllSourceOfficial"),
                S("Settings_DllSourceMatchesEmbedded"),
                deployedHash,
                embeddedHash,
                false);
        }

        return new DllIdentityInfo(
            DllSourceKind.HashMismatch,
            S("Settings_DllSourceHashMismatch"),
            embeddedIsCustom
                ? S("Settings_DllSourceDeployCustomHint")
                : S("Settings_DllSourceHashMismatchHint"),
            deployedHash,
            embeddedHash,
            embeddedIsCustom);
    }

    private static bool IsCustomHash(string? hash)
        => string.Equals(hash, CustomWebDavHash, StringComparison.OrdinalIgnoreCase);

    private static bool EmbeddedDllContainsCustomMarker()
    {
        try
        {
            using var stream = typeof(EmbeddedDll).Assembly.GetManifestResourceStream(EmbeddedDllResourceName);
            return stream != null && StreamContainsCustomMarker(stream);
        }
        catch
        {
            return false;
        }
    }

    private static bool FileContainsCustomMarker(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return StreamContainsCustomMarker(fs);
    }

    private static bool StreamContainsCustomMarker(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        foreach (var marker in CustomBuildMarkers)
        {
            if (IndexOf(bytes, marker) >= 0)
                return true;
        }
        return false;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }

        return -1;
    }

    private static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static string S(string key) => Resources.S.Get(key);
}
