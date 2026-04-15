using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RandVideoPlayer.Library;

public sealed class ShuffleFile
{
    [JsonPropertyName("seed")] public uint Seed { get; set; }
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
}

public sealed class PositionFile
{
    [JsonPropertyName("currentIndex")] public int CurrentIndex { get; set; } = -1;
    [JsonPropertyName("currentFileRelative")] public string? CurrentFileRelative { get; set; }
    // PositionMs is only populated when the app exits while a file is playing.
    // Normal track-change writes set this to 0 so that coming back to a file
    // (after playing others) restarts it from the beginning.
    [JsonPropertyName("positionMs")] public long PositionMs { get; set; }
}

public static class PlaylistState
{
    public const string ShuffleFileName = ".rvp_shuffle.json";
    public const string PositionFileName = ".rvp_position.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string ShufflePath(string folder) => Path.Combine(folder, ShuffleFileName);
    public static string PositionPath(string folder) => Path.Combine(folder, PositionFileName);

    public static ShuffleFile? LoadShuffle(string folder)
    {
        var path = ShufflePath(folder);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<ShuffleFile>(fs, JsonOpts);
        }
        catch { return null; }
    }

    public static PositionFile? LoadPosition(string folder)
    {
        var path = PositionPath(folder);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<PositionFile>(fs, JsonOpts);
        }
        catch { return null; }
    }

    public static void SaveShuffle(string folder, ShuffleFile data)
    {
        WriteAtomic(ShufflePath(folder), data);
        TrySetHidden(ShufflePath(folder));
    }

    public static void SavePosition(string folder, PositionFile data)
    {
        WriteAtomic(PositionPath(folder), data);
        TrySetHidden(PositionPath(folder));
    }

    private static void WriteAtomic<T>(string finalPath, T data)
    {
        var dir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(dir);
        var tmp = finalPath + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, data, JsonOpts);
        }
        if (File.Exists(finalPath))
        {
            // Clear hidden before Replace — Replace can fail on hidden targets.
            try { File.SetAttributes(finalPath, FileAttributes.Normal); } catch { }
            File.Replace(tmp, finalPath, null);
        }
        else
        {
            File.Move(tmp, finalPath);
        }
    }

    private static void TrySetHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch { /* best-effort */ }
    }
}
