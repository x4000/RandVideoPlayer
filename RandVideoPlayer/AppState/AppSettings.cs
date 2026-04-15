using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RandVideoPlayer.AppState;

// Settings are persisted across two JSON files so that the high-churn / UI-state
// values (window size, sidebar toggles, volume, etc.) live separately from the
// "what are we working on" core state (last folder, recent folders, theme):
//   settings.json      -> lastFolder, recent, darkMode
//   settings-user.json -> everything else
public sealed class AppSettings
{
    public const int MaxRecent = 15;

    // Core (settings.json)
    [JsonPropertyName("lastFolder")] public string? LastFolder { get; set; }
    [JsonPropertyName("recent")] public List<string> Recent { get; set; } = new();
    [JsonPropertyName("darkMode")] public bool DarkMode { get; set; } = true;

    // User (settings-user.json)
    [JsonPropertyName("volume")] public int Volume { get; set; } = 80;
    [JsonPropertyName("muted")] public bool Muted { get; set; }
    [JsonPropertyName("sidebarVisible")] public bool SidebarVisible { get; set; } = true;
    [JsonPropertyName("sidebarShowShuffleOrder")] public bool SidebarShowShuffleOrder { get; set; } = true;
    [JsonPropertyName("errorPanelVisible")] public bool ErrorPanelVisible { get; set; }
    [JsonPropertyName("windowBounds")] public WindowBounds? WindowBounds { get; set; }

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ArcenSettings", "RandVideoPlayer");
    public static string CorePath => Path.Combine(SettingsDir, "settings.json");
    public static string UserPath => Path.Combine(SettingsDir, "settings-user.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        var s = new AppSettings();
        // Core
        try
        {
            if (File.Exists(CorePath))
            {
                var core = JsonSerializer.Deserialize<CoreDto>(File.ReadAllText(CorePath), JsonOpts);
                if (core != null)
                {
                    s.LastFolder = core.LastFolder;
                    s.Recent = core.Recent ?? new();
                    s.DarkMode = core.DarkMode;
                }
            }
        }
        catch { }
        // User
        try
        {
            if (File.Exists(UserPath))
            {
                var u = JsonSerializer.Deserialize<UserDto>(File.ReadAllText(UserPath), JsonOpts);
                if (u != null)
                {
                    s.Volume = u.Volume;
                    s.Muted = u.Muted;
                    s.SidebarVisible = u.SidebarVisible;
                    s.SidebarShowShuffleOrder = u.SidebarShowShuffleOrder;
                    s.ErrorPanelVisible = u.ErrorPanelVisible;
                    s.WindowBounds = u.WindowBounds;
                }
            }
            else if (File.Exists(CorePath))
            {
                // One-shot migration: if an older settings.json has the user fields,
                // carry them across into the new split layout.
                try
                {
                    var migrated = JsonSerializer.Deserialize<MigrationDto>(File.ReadAllText(CorePath), JsonOpts);
                    if (migrated != null)
                    {
                        if (migrated.Volume.HasValue) s.Volume = migrated.Volume.Value;
                        if (migrated.Muted.HasValue) s.Muted = migrated.Muted.Value;
                        if (migrated.SidebarVisible.HasValue) s.SidebarVisible = migrated.SidebarVisible.Value;
                        if (migrated.SidebarShowShuffleOrder.HasValue) s.SidebarShowShuffleOrder = migrated.SidebarShowShuffleOrder.Value;
                        if (migrated.ErrorPanelVisible.HasValue) s.ErrorPanelVisible = migrated.ErrorPanelVisible.Value;
                        if (migrated.WindowBounds != null) s.WindowBounds = migrated.WindowBounds;
                    }
                }
                catch { }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            WriteJsonAtomic(CorePath, new CoreDto
            {
                LastFolder = LastFolder,
                Recent = Recent,
                DarkMode = DarkMode
            });
            WriteJsonAtomic(UserPath, new UserDto
            {
                Volume = Volume,
                Muted = Muted,
                SidebarVisible = SidebarVisible,
                SidebarShowShuffleOrder = SidebarShowShuffleOrder,
                ErrorPanelVisible = ErrorPanelVisible,
                WindowBounds = WindowBounds
            });
        }
        catch { }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public void MarkFolderUsed(string folder)
    {
        LastFolder = folder;
        Recent.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, folder);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }

    // ------- DTOs used purely for on-disk shape ------------------------------

    private sealed class CoreDto
    {
        [JsonPropertyName("lastFolder")] public string? LastFolder { get; set; }
        [JsonPropertyName("recent")] public List<string>? Recent { get; set; }
        [JsonPropertyName("darkMode")] public bool DarkMode { get; set; } = true;
    }

    private sealed class UserDto
    {
        [JsonPropertyName("volume")] public int Volume { get; set; } = 80;
        [JsonPropertyName("muted")] public bool Muted { get; set; }
        [JsonPropertyName("sidebarVisible")] public bool SidebarVisible { get; set; } = true;
        [JsonPropertyName("sidebarShowShuffleOrder")] public bool SidebarShowShuffleOrder { get; set; } = true;
        [JsonPropertyName("errorPanelVisible")] public bool ErrorPanelVisible { get; set; }
        [JsonPropertyName("windowBounds")] public WindowBounds? WindowBounds { get; set; }
    }

    // Used only to read the old unified format when migrating forward.
    private sealed class MigrationDto
    {
        [JsonPropertyName("volume")] public int? Volume { get; set; }
        [JsonPropertyName("muted")] public bool? Muted { get; set; }
        [JsonPropertyName("sidebarVisible")] public bool? SidebarVisible { get; set; }
        [JsonPropertyName("sidebarShowShuffleOrder")] public bool? SidebarShowShuffleOrder { get; set; }
        [JsonPropertyName("errorPanelVisible")] public bool? ErrorPanelVisible { get; set; }
        [JsonPropertyName("windowBounds")] public WindowBounds? WindowBounds { get; set; }
    }
}

public sealed class WindowBounds
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }
}
