using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CloudRedirect.Services;

internal sealed record OpenSteamToolCheckItem(string Name, bool Ok, string Detail, bool Critical = true);

internal sealed record OpenSteamToolCheckResult(
    bool IsReady,
    IReadOnlyList<OpenSteamToolCheckItem> Items,
    IReadOnlyList<string> LuaDirectories,
    int LuaFileCount,
    int NamespaceAppCount,
    SteamLoginUser? CurrentUser);

internal static class OpenSteamToolIntegration
{
    public const string LoaderFileName = "cloud_redirect_loader.lua";

    private const string LoaderContent = """
do
  local candidates = {
    "cloud_redirect.dll",
    ".\\cloud_redirect.dll",
    "config\\lua\\..\\..\\cloud_redirect.dll"
  }
  local last_error = nil
  for _, path in ipairs(candidates) do
    local entry, err = package.loadlib(path, "CloudRedirectLua")
    if entry then
      local ok, call_error = pcall(entry)
      if not ok then
        error("CloudRedirectLua failed: " .. tostring(call_error))
      end
      _G.cloudredirect_loaded = true
      return
    end
    last_error = err
  end
  error("CloudRedirect loader could not load cloud_redirect.dll: " .. tostring(last_error))
end
""";

    public static bool IsInstalled(string steamPath)
    {
        return File.Exists(Path.Combine(steamPath, "OpenSteamTool.dll")) &&
               (File.Exists(Path.Combine(steamPath, "dwmapi.dll")) ||
                File.Exists(Path.Combine(steamPath, "xinput1_4.dll")));
    }

    public static string GetDefaultLuaDir(string steamPath)
    {
        return Path.Combine(steamPath, "config", "lua");
    }

    public static string GetLoaderPath(string steamPath)
    {
        return Path.Combine(GetDefaultLuaDir(steamPath), LoaderFileName);
    }

    public static string GetExpectedLoaderContent() => LoaderContent.Replace("\r\n", "\n");

    public static bool IsLoaderCurrent(string steamPath)
    {
        var path = GetLoaderPath(steamPath);
        if (!File.Exists(path)) return false;

        try
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            return string.Equals(text, GetExpectedLoaderContent(), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void InstallLoader(string steamPath)
    {
        var luaDir = GetDefaultLuaDir(steamPath);
        Directory.CreateDirectory(luaDir);
        FileUtils.AtomicWriteAllText(GetLoaderPath(steamPath), GetExpectedLoaderContent());
    }

    public static void RemoveLoader(string steamPath)
    {
        var path = GetLoaderPath(steamPath);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static OpenSteamToolCheckResult CheckInstallation(string steamPath)
    {
        var items = new List<OpenSteamToolCheckItem>();

        var ostDll = Path.Combine(steamPath, "OpenSteamTool.dll");
        var dwmapi = Path.Combine(steamPath, "dwmapi.dll");
        var xinput = Path.Combine(steamPath, "xinput1_4.dll");
        var cloudDll = Path.Combine(steamPath, "cloud_redirect.dll");
        var loader = GetLoaderPath(steamPath);
        var legacySteamToolsLuaDir = Path.Combine(steamPath, "config", "stplug-in");

        Add(items, File.Exists(ostDll), "OpenSteamTool.dll", ostDll);
        Add(items, File.Exists(dwmapi) || File.Exists(xinput),
            "OpenSteamTool proxy DLL", File.Exists(dwmapi) ? dwmapi : xinput);
        Add(items, File.Exists(cloudDll), "cloud_redirect.dll", cloudDll);
        Add(items, File.Exists(loader), LoaderFileName, loader);
        Add(items, IsLoaderCurrent(steamPath), "Lua loader content",
            IsLoaderCurrent(steamPath) ? "Current" : "Missing or stale");

        var luaDirs = GetLuaDirectories(steamPath);
        int luaFiles = 0;
        int namespaceApps = 0;
        foreach (var dir in luaDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*.lua"))
            {
                luaFiles++;
                var stem = Path.GetFileNameWithoutExtension(file);
                if (uint.TryParse(stem, out var appId) &&
                    CloudCleanup.IsSelfUnlockingLua(file, appId))
                {
                    namespaceApps++;
                }
            }
        }

        Add(items, luaDirs.Any(Directory.Exists), "Lua directory",
            string.Join("; ", luaDirs));
        Add(items, namespaceApps > 0, "Namespace Lua apps",
            $"{namespaceApps} self-unlocking / {luaFiles} lua files");

        var legacyLuaCount = Directory.Exists(legacySteamToolsLuaDir)
            ? Directory.GetFiles(legacySteamToolsLuaDir, "*.lua").Length
            : 0;
        Add(items, legacyLuaCount == 0, "Legacy stplug-in Lua folder",
            legacyLuaCount == 0
                ? "No lua files in the SteamTools-only stplug-in directory"
                : $"{legacyLuaCount} lua file(s) found in config\\stplug-in; OpenSteamTool does not read this directory",
            critical: false);

        var currentUser = SteamAccountService.GetMostRecentUser(steamPath);
        Add(items, currentUser != null, "Steam account",
            currentUser != null
                ? $"{currentUser.PersonaName} ({currentUser.AccountId})"
                : "No unambiguous MostRecent user in loginusers.vdf");

        var ready = items.All(i => !i.Critical || i.Ok);
        return new OpenSteamToolCheckResult(ready, items, luaDirs, luaFiles, namespaceApps, currentUser);
    }

    public static IReadOnlyList<string> GetLuaDirectories(string steamPath)
    {
        var dirs = new List<string>();
        AddUnique(dirs, GetDefaultLuaDir(steamPath));

        var toml = Path.Combine(steamPath, "opensteamtool.toml");
        if (!File.Exists(toml))
            return dirs;

        try
        {
            bool inLua = false;
            var pending = new StringBuilder();
            foreach (var rawLine in File.ReadLines(toml))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inLua = string.Equals(line, "[lua]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inLua)
                    continue;

                if (pending.Length == 0)
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line[..eq].Trim();
                    if (!string.Equals(key, "paths", StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Append(line[(eq + 1)..]);
                }
                else
                {
                    pending.Append('\n').Append(line);
                }

                if (!pending.ToString().Contains(']'))
                    continue;

                foreach (var path in ParseQuotedArray(pending.ToString()))
                    AddUnique(dirs, NormalizeLuaPath(steamPath, path));
                pending.Clear();
            }
        }
        catch
        {
            // Ignore malformed optional config; OpenSteamTool itself falls back to defaults.
        }

        return dirs;
    }

    private static IEnumerable<string> ParseQuotedArray(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false;
        bool escape = false;

        foreach (var c in text)
        {
            if (!inQuote)
            {
                if (c == '"')
                {
                    inQuote = true;
                    current.Clear();
                }
                continue;
            }

            if (escape)
            {
                current.Append(c switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => c
                });
                escape = false;
                continue;
            }

            if (c == '\\')
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inQuote = false;
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        return result;
    }

    private static string NormalizeLuaPath(string steamPath, string path)
    {
        path = path.Trim();
        if (path.Length == 0) return path;
        path = path.Replace('/', Path.DirectorySeparatorChar);

        if (!Path.IsPathRooted(path))
            path = Path.Combine(steamPath, path);

        return Path.GetFullPath(path);
    }

    private static void AddUnique(List<string> dirs, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        foreach (var existing in dirs)
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                return;
        dirs.Add(path);
    }

    private static void Add(List<OpenSteamToolCheckItem> items, bool ok, string name, string detail, bool critical = true)
    {
        items.Add(new OpenSteamToolCheckItem(name, ok, detail, critical));
    }
}
