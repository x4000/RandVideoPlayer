using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace RandVideoPlayer.Library;

// Background duration scanner. Uses LibVLC's Media.Parse to get duration per file.
// Caches results (by full path + size + mtime) in a JSON file inside the folder.
public sealed class DurationIndex : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly bool _ownsLibVlc;
    private readonly string _folder;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private readonly object _lock = new();

    public event Action? Updated;

    public int FileCount { get; private set; }
    public long TotalDurationMs { get; private set; }
    public int ScannedCount { get; private set; }
    public bool Scanning { get; private set; }

    private sealed class CacheEntry
    {
        public long Size { get; set; }
        public long MTimeTicks { get; set; }
        public long DurationMs { get; set; }
    }

    // Always uses a dedicated LibVLC instance so that metadata parsing never
    // contends with the main player's audio pipeline or event dispatch.
    public DurationIndex(string folder)
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--quiet", "--no-video", "--no-audio");
        _ownsLibVlc = true;
        _folder = folder;
        _cachePath = Path.Combine(folder, ".rvp_durations.json");
        LoadCache();
    }

    public void StartOrUpdate(IReadOnlyList<string> fullPaths)
    {
        Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        FileCount = fullPaths.Count;
        Scanning = true;
        ScannedCount = 0;
        // Initial total comes from cached hits.
        TotalDurationMs = 0;
        foreach (var p in fullPaths)
        {
            if (_cache.TryGetValue(p, out var e) && StillValid(p, e))
            {
                TotalDurationMs += e.DurationMs;
                ScannedCount++;
            }
        }
        RaiseUpdated();
        _worker = Task.Run(() => ScanLoop(fullPaths, token), token);
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        try { _worker?.Wait(500); } catch { }
        _cts = null; _worker = null;
    }

    private void ScanLoop(IReadOnlyList<string> paths, CancellationToken token)
    {
        try
        {
            foreach (var path in paths)
            {
                if (token.IsCancellationRequested) break;
                if (_cache.TryGetValue(path, out var e) && StillValid(path, e)) continue; // already counted
                long durMs = ProbeDuration(path);
                var fi = new FileInfo(path);
                var entry = new CacheEntry
                {
                    Size = fi.Exists ? fi.Length : 0,
                    MTimeTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0,
                    DurationMs = Math.Max(0, durMs)
                };
                _cache[path] = entry;
                lock (_lock)
                {
                    ScannedCount++;
                    TotalDurationMs += entry.DurationMs;
                }
                // Batch updates a bit.
                if (ScannedCount % 8 == 0) RaiseUpdated();
            }
        }
        catch { /* ignore */ }
        finally
        {
            Scanning = false;
            SaveCache();
            RaiseUpdated();
        }
    }

    private long ProbeDuration(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var media = new Media(_libVlc, new Uri(path));
            using var ev = new ManualResetEventSlim(false);
            void H(object? s, MediaParsedChangedEventArgs e) { ev.Set(); }
            media.ParsedChanged += H;
            try
            {
                var status = media.Parse(MediaParseOptions.ParseLocal, timeout: 4000).GetAwaiter().GetResult();
                return media.Duration > 0 ? media.Duration : 0;
            }
            finally
            {
                media.ParsedChanged -= H;
            }
        }
        catch { return 0; }
    }

    private static bool StillValid(string path, CacheEntry e)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            return fi.Length == e.Size && fi.LastWriteTimeUtc.Ticks == e.MTimeTicks;
        }
        catch { return false; }
    }

    private void RaiseUpdated()
    {
        try { Updated?.Invoke(); } catch { }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            if (dict == null) return;
            foreach (var kv in dict) _cache[kv.Key] = kv.Value;
        }
        catch { }
    }

    private void SaveCache()
    {
        try
        {
            var dict = new Dictionary<string, CacheEntry>(_cache, StringComparer.OrdinalIgnoreCase);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(dict));
            if (File.Exists(_cachePath))
            {
                try { File.SetAttributes(_cachePath, FileAttributes.Normal); } catch { }
                File.Replace(tmp, _cachePath, null);
            }
            else File.Move(tmp, _cachePath);
            try { File.SetAttributes(_cachePath, File.GetAttributes(_cachePath) | FileAttributes.Hidden); } catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        Cancel();
        if (_ownsLibVlc)
        {
            try { _libVlc.Dispose(); } catch { }
        }
    }
}
