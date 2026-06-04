using System;
using System.IO;

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
        "EDC170BAEA0D549700230F8845D334C949F05B4764828D4C3B10F919D3B5A378";

    public static DllIdentityInfo GetCurrent()
    {
        var steamPath = SteamDetector.FindSteamPath();
        var embeddedHash = EmbeddedDll.GetEmbeddedHash();

        if (steamPath == null)
        {
            return new DllIdentityInfo(
                DllSourceKind.Unknown,
                S("Settings_DllSourceUnknown"),
                S("Settings_DllSourceSteamMissing"),
                null,
                embeddedHash,
                IsCustomHash(embeddedHash));
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
                IsCustomHash(embeddedHash));
        }

        string? deployedHash;
        try
        {
            deployedHash = ComputeSha256(dllPath);
        }
        catch
        {
            return new DllIdentityInfo(
                DllSourceKind.Unknown,
                S("Settings_DllSourceUnknown"),
                S("Settings_DllSourceReadFailed"),
                null,
                embeddedHash,
                IsCustomHash(embeddedHash));
        }

        var deployedIsCustom = IsCustomHash(deployedHash);
        var embeddedIsCustom = IsCustomHash(embeddedHash);
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

    private static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static string S(string key) => Resources.S.Get(key);
}
