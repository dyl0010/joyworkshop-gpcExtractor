using System;
using System.IO;
using System.Text.Json;

namespace GpcSessionBrowser;

public sealed record GpcTerminalSettings(string TerminalPath, string ConfigEditorPath)
{
    public const string DefaultTerminalPath = @"C:\Program Files\PowerShell\7\pwsh.exe";
    public const string DefaultConfigEditorPath = "code";

    public static GpcTerminalSettings Default { get; } = new(DefaultTerminalPath, DefaultConfigEditorPath);

    public static string GetSettingsFilePath(string localApplicationData)
    {
        return Path.Combine(localApplicationData, "GpcSessionBrowser", "settings.json");
    }

    public static GpcTerminalSettings Load(string localApplicationData)
    {
        try
        {
            var filePath = GetSettingsFilePath(localApplicationData);
            if (!File.Exists(filePath))
            {
                return Default;
            }

            var settings = JsonSerializer.Deserialize<GpcTerminalSettings>(File.ReadAllText(filePath));
            return string.IsNullOrWhiteSpace(settings?.TerminalPath)
                ? Default
                : settings with
                {
                    TerminalPath = settings.TerminalPath.Trim(),
                    ConfigEditorPath = string.IsNullOrWhiteSpace(settings.ConfigEditorPath)
                        ? DefaultConfigEditorPath
                        : settings.ConfigEditorPath.Trim()
                };
        }
        catch
        {
            return Default;
        }
    }

    public void Save(string localApplicationData)
    {
        var filePath = GetSettingsFilePath(localApplicationData);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this));
    }
}
