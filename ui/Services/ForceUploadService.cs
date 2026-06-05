using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CloudRedirect.Resources;

namespace CloudRedirect.Services;

internal sealed record ForceUploadSummary(
    string AccountId,
    string AppId,
    bool Success,
    int Scanned,
    int Uploaded,
    int Skipped,
    int Failed,
    long TotalBytes,
    long OldChangeNumber,
    long NewChangeNumber,
    bool ManifestPublished,
    bool Drained,
    bool TokensPublished,
    string SourcePath,
    string? Error,
    string Details);

internal static class ForceUploadService
{
    public static async Task<ForceUploadSummary> ForceUploadLocalAppAsync(
        string steamPath,
        string accountId,
        string appId,
        CancellationToken cancellationToken = default)
    {
        var config = SteamDetector.ReadConfig()
            ?? throw new InvalidOperationException(S.Get("ForceUpload_ConfigMissing"));
        if (config.IsLocal)
            throw new InvalidOperationException(S.Get("ForceUpload_LocalOnly"));

        var provider = config.Provider;
        if (string.Equals(provider, "folder", StringComparison.OrdinalIgnoreCase))
            provider = "folder";

        var cliPath = EmbeddedCli.EnsureExtracted();
        if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
            throw new InvalidOperationException(S.Get("ForceUpload_CliMissing"));

        var cloudRoot = Path.Combine(steamPath, "cloud_redirect");
        var args = new[]
        {
            "force-upload-local-app",
            provider,
            accountId,
            appId,
            cloudRoot,
            steamPath
        };

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (TryParseSummary(stdout, accountId, appId, out var summary, out var parseError))
            return summary;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? S.Format("ForceUpload_CliFailedFormat", process.ExitCode, stdout.Trim())
                : S.Format("ForceUpload_CliFailedFormat", process.ExitCode, stderr.Trim()));

        throw new InvalidOperationException(parseError ?? S.Format("ForceUpload_CliFailedFormat",
            process.ExitCode, stdout.Trim()));
    }

    public static string FormatSummary(ForceUploadSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine(S.Format("ForceUpload_ResultSummaryFormat",
            summary.AppId, summary.Uploaded, summary.Scanned, summary.Skipped, summary.Failed));
        sb.AppendLine(S.Format("ForceUpload_ResultBytesFormat", summary.TotalBytes));
        sb.AppendLine(S.Format("ForceUpload_ResultCnFormat",
            summary.OldChangeNumber, summary.NewChangeNumber));
        sb.AppendLine(S.Format("ForceUpload_ResultSourceFormat", summary.SourcePath));
        sb.AppendLine(S.Format("ForceUpload_ResultFlagsFormat",
            summary.ManifestPublished, summary.Drained, summary.TokensPublished));
        if (!string.IsNullOrWhiteSpace(summary.Error))
            sb.AppendLine(S.Format("ForceUpload_ResultErrorFormat", summary.Error));
        sb.AppendLine();
        sb.Append(summary.Details);
        return sb.ToString();
    }

    private static bool TryParseSummary(
        string stdout,
        string accountId,
        string appId,
        out ForceUploadSummary summary,
        out string? parseError)
    {
        summary = null!;
        parseError = null;
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var success = ReadBool(root, "success");
            var error = ReadString(root, "error");
            if (!success && string.IsNullOrWhiteSpace(error))
                error = S.Get("ForceUpload_NotClean");

            summary = new ForceUploadSummary(
                accountId,
                appId,
                success,
                ReadInt(root, "scanned"),
                ReadInt(root, "uploaded"),
                ReadInt(root, "skipped"),
                ReadInt(root, "failed"),
                ReadLong(root, "total_bytes"),
                ReadLong(root, "old_cn"),
                ReadLong(root, "new_cn"),
                ReadBool(root, "manifest_published"),
                ReadBool(root, "drained"),
                ReadBool(root, "tokens_published"),
                ReadString(root, "source") ?? "",
                error,
                ReadString(root, "details") ?? "");
            return true;
        }
        catch (JsonException ex)
        {
            parseError = $"Invalid CLI response: {ex.Message}";
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) &&
           prop.ValueKind == JsonValueKind.True;

    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value)
            ? value
            : 0;

    private static long ReadLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var value)
            ? value
            : 0;
}
