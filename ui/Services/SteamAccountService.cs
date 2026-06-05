using System;
using System.Collections.Generic;
using System.IO;

namespace CloudRedirect.Services;

internal sealed record SteamLoginUser(
    ulong SteamId64,
    uint AccountId,
    string AccountName,
    string PersonaName,
    bool MostRecent);

internal static class SteamAccountService
{
    private const ulong SteamId64Base = 76561197960265728UL;

    public static IReadOnlyList<SteamLoginUser> GetLoginUsers(string steamPath)
    {
        var users = new List<SteamLoginUser>();
        var path = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(path))
            return users;

        ulong currentSteamId = 0;
        string accountName = "";
        string personaName = "";
        bool mostRecent = false;
        bool inUser = false;
        int braceDepth = 0;

        void FlushUser()
        {
            if (currentSteamId <= SteamId64Base)
                return;

            users.Add(new SteamLoginUser(
                currentSteamId,
                unchecked((uint)(currentSteamId & 0xFFFFFFFFUL)),
                accountName,
                personaName,
                mostRecent));
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line == "{")
            {
                braceDepth++;
                continue;
            }

            if (line == "}")
            {
                if (braceDepth == 2 && inUser)
                {
                    FlushUser();
                    currentSteamId = 0;
                    accountName = "";
                    personaName = "";
                    mostRecent = false;
                    inUser = false;
                }

                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }

            if (braceDepth == 1 && TryParseQuotedSteamId(line, out var sid))
            {
                currentSteamId = sid;
                accountName = "";
                personaName = "";
                mostRecent = false;
                inUser = true;
                continue;
            }

            if (!inUser || braceDepth != 2)
                continue;

            if (!TryParseKeyValue(line, out var key, out var value))
                continue;

            if (string.Equals(key, "AccountName", StringComparison.OrdinalIgnoreCase))
                accountName = value;
            else if (string.Equals(key, "PersonaName", StringComparison.OrdinalIgnoreCase))
                personaName = value;
            else if (string.Equals(key, "MostRecent", StringComparison.OrdinalIgnoreCase))
                mostRecent = value == "1";
        }

        return users;
    }

    public static SteamLoginUser? GetMostRecentUser(string steamPath)
    {
        var users = GetLoginUsers(steamPath);
        foreach (var user in users)
            if (user.MostRecent)
                return user;

        return users.Count == 1 ? users[0] : null;
    }

    private static bool TryParseQuotedSteamId(string line, out ulong steamId)
    {
        steamId = 0;
        if (!TryParseKey(line, out var key))
            return false;

        if (!ulong.TryParse(key, out var sid) || sid <= SteamId64Base)
            return false;

        steamId = sid;
        return true;
    }

    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        key = "";
        value = "";
        if (!TryParseKey(line, out key, out var keyEnd))
            return false;

        var rest = line[keyEnd..].TrimStart();
        if (!TryParseKey(rest, out value))
            return false;

        return true;
    }

    private static bool TryParseKey(string line, out string key)
        => TryParseKey(line, out key, out _);

    private static bool TryParseKey(string line, out string key, out int endIndex)
    {
        key = "";
        endIndex = 0;
        if (line.Length < 2 || line[0] != '"')
            return false;

        var end = line.IndexOf('"', 1);
        if (end < 0)
            return false;

        key = line[1..end];
        endIndex = end + 1;
        return true;
    }
}
