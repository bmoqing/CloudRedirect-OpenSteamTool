using System.IO;
using System.Text.Json;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CloudRedirect.Services;

internal static class AppThemeSettings
{
    private const string ThemeKey = "theme";
    private const string Light = "light";
    private const string Dark = "dark";

    public static ApplicationTheme ReadTheme()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return ApplicationTheme.Dark;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(ThemeKey, out var prop))
                return ApplicationTheme.Dark;

            return string.Equals(prop.GetString(), Light, StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
        }
        catch
        {
            return ApplicationTheme.Dark;
        }
    }

    public static void ApplySavedTheme()
    {
        ApplicationThemeManager.Apply(ReadTheme(), WindowBackdropType.None, updateAccent: true);
    }

    public static void ApplyAndSave(ApplicationTheme theme)
    {
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
        SaveTheme(theme);
    }

    public static ApplicationTheme Toggle()
    {
        var next = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;
        ApplyAndSave(next);
        return next;
    }

    public static string ToSettingValue(ApplicationTheme theme)
    {
        return theme == ApplicationTheme.Light ? Light : Dark;
    }

    private static void SaveTheme(ApplicationTheme theme)
    {
        var path = GetSettingsPath();
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        JsonElement existing = default;
        if (File.Exists(path))
        {
            try
            {
                using var oldDoc = JsonDocument.Parse(File.ReadAllText(path));
                existing = oldDoc.RootElement.Clone();
            }
            catch { }
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(ThemeKey, ToSettingValue(theme));

            if (existing.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (prop.Name == ThemeKey)
                        continue;
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        FileUtils.AtomicWriteAllText(path, json);
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(SteamDetector.GetConfigDir(), "settings.json");
    }
}
