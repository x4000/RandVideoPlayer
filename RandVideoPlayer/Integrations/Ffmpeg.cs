using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RandVideoPlayer.Integrations;

/// <summary>
/// Thin wrapper around a locally-installed ffmpeg / ffprobe. Used by the in-app
/// cut tool. All calls spawn a child process and BLOCK the calling thread until
/// it exits, so callers must invoke them off the UI thread (MainForm uses a
/// background Task). Nothing here touches libvlc.
/// </summary>
public static class Ffmpeg
{
    private static string? _ffmpeg;
    private static string? _ffprobe;
    private static bool _checked;

    public static string? FfmpegPath { get { EnsureFound(); return _ffmpeg; } }
    public static string? FfprobePath { get { EnsureFound(); return _ffprobe; } }
    public static bool IsAvailable => FfmpegPath != null && FfprobePath != null;

    private static void EnsureFound()
    {
        if (_checked) return;
        _checked = true;
        _ffmpeg = Locate("ffmpeg.exe");
        _ffprobe = Locate("ffprobe.exe");
    }

    private static string? Locate(string exe)
    {
        // 1) winget "Links" shims (where the user's install lives).
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links", exe),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\ffmpeg\bin\" + exe),
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\chocolatey\bin\" + exe),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;

        // 2) Anything on PATH.
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
                catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    public sealed class MediaInfo
    {
        public double DurationSec;
        public string? VideoCodec;
        public string? AudioCodec;
        public int Width;
        public int Height;
        public string? PixFmt;
        public double Fps = 30.0;
        public int AudioStreams;
        public int SampleRate;
        public int Channels;
        public int AudioBitrateKbps;
        public bool HasVideo => !string.IsNullOrEmpty(VideoCodec);
        public bool HasAudio => !string.IsNullOrEmpty(AudioCodec);
    }

    /// <summary>ffprobe the container: duration, codecs, dimensions, fps. Null on failure.</summary>
    public static MediaInfo? Probe(string input)
    {
        var probe = FfprobePath;
        if (probe == null) return null;
        var args = "-v error -show_entries format=duration"
                 + ":stream=index,codec_type,codec_name,width,height,pix_fmt,avg_frame_rate"
                 + ",sample_rate,channels,bit_rate"
                 + " -of json " + Quote(input);
        if (!RunToString(probe, args, out var json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var info = new MediaInfo();

            if (root.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var dur)
                && double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                info.DurationSec = d;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    if (type == "video" && info.VideoCodec == null)
                    {
                        info.VideoCodec = codec;
                        if (s.TryGetProperty("width", out var w)) info.Width = w.GetInt32();
                        if (s.TryGetProperty("height", out var h)) info.Height = h.GetInt32();
                        if (s.TryGetProperty("pix_fmt", out var p)) info.PixFmt = p.GetString();
                        if (s.TryGetProperty("avg_frame_rate", out var fr))
                            info.Fps = ParseRate(fr.GetString());
                    }
                    else if (type == "audio")
                    {
                        info.AudioStreams++;
                        if (info.AudioCodec != null) continue;
                        info.AudioCodec = codec;
                        if (s.TryGetProperty("sample_rate", out var sr)
                            && int.TryParse(sr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sri))
                            info.SampleRate = sri;
                        if (s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number)
                            info.Channels = ch.GetInt32();
                        if (s.TryGetProperty("bit_rate", out var br)
                            && long.TryParse(br.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bri) && bri > 0)
                            info.AudioBitrateKbps = (int)Math.Round(bri / 1000.0);
                    }
                }
            }
            return info;
        }
        catch { return null; }
    }

    private static double ParseRate(string? rate)
    {
        // avg_frame_rate arrives as "num/den" (e.g. "24000/1001").
        if (string.IsNullOrEmpty(rate)) return 30.0;
        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0 && n > 0)
            return n / den;
        return 30.0;
    }

    /// <summary>
    /// Video keyframe (I-frame) timestamps in seconds, ascending. Reads the whole
    /// file's packet index, so it can take a second or two — call off the UI
    /// thread. Empty list on failure (callers fall back to raw timestamps).
    /// </summary>
    public static List<double> GetKeyframes(string input)
    {
        var result = new List<double>();
        var probe = FfprobePath;
        if (probe == null) return result;
        var args = "-v error -select_streams v:0 -skip_frame nokey "
                 + "-show_entries frame=best_effort_timestamp_time -of csv=p=0 " + Quote(input);
        if (!RunToString(probe, args, out var outp)) return result;
        foreach (var line in outp.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
        }
        result.Sort();
        return result;
    }

    /// <summary>Largest keyframe time &lt;= targetSec, or 0 if none precede it.</summary>
    public static double KeyframeAtOrBefore(IReadOnlyList<double> keyframes, double targetSec)
    {
        double best = 0;
        foreach (var kf in keyframes)
        {
            if (kf <= targetSec + 0.0005) best = kf;
            else break;
        }
        return best;
    }

    /// <summary>
    /// Lossless cut via stream copy. <paramref name="startKeyframeSec"/> MUST be an
    /// actual keyframe time (see <see cref="KeyframeAtOrBefore"/>) so the copied
    /// stream begins on a decodable I-frame. No re-encode — bit-for-bit identical.
    /// </summary>
    public static bool CutLossless(string input, double startKeyframeSec, double durationSec,
                                   string output, Action<double>? progress, CancellationToken ct, out string error)
    {
        var args = "-hide_banner -y "
                 + "-ss " + Sec(startKeyframeSec) + " -i " + Quote(input) + " -t " + Sec(durationSec)
                 + " -c copy -map 0 "
                 + "-progress pipe:1 -nostats " + Quote(output);
        return RunFfmpeg(args, durationSec, progress, ct, out error);
    }

    /// <summary>
    /// Frame-accurate cut by re-encoding the selection at near-visually-lossless
    /// quality (CRF 18). NOT lossless, but exact to the requested in/out. Keeps
    /// the source container, codecs, resolution and pixel format.
    /// </summary>
    public static bool CutReencode(string input, double inSec, double outSec, string pixFmt,
                                   string output, Action<double>? progress, CancellationToken ct, out string error)
    {
        var pf = string.IsNullOrEmpty(pixFmt) ? "yuv420p" : pixFmt;
        var args = "-hide_banner -y -i " + Quote(input)
                 + " -ss " + Sec(inSec) + " -to " + Sec(outSec)
                 + " -c:v libx264 -preset medium -crf 18 -pix_fmt " + pf
                 + " -c:a aac -b:a 192k -movflags +faststart "
                 + "-progress pipe:1 -nostats " + Quote(output);
        return RunFfmpeg(args, Math.Max(0.001, outSec - inSec), progress, ct, out error);
    }

    // ---- process plumbing ----------------------------------------------------

    internal static string Quote(string p) => "\"" + p + "\"";
    internal static string Sec(double s) => s.ToString("F6", CultureInfo.InvariantCulture);

    // Runs ffprobe and returns its stdout as a string. Returns false on non-zero exit.
    private static bool RunToString(string exe, string args, out string stdout)
    {
        stdout = "";
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                }
            };
            p.Start();
            stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Runs ffmpeg, streaming -progress from stdout to report a 0..1 fraction, and
    // keeping a tail of stderr for error reporting. Kills the process if the
    // cancellation token fires. Returns true on exit code 0.
    internal static bool RunFfmpeg(string args, double totalSec, Action<double>? progress,
                                   CancellationToken ct, out string error)
        => RunFfmpeg(args, totalSec, progress, ct, null, out error);

    // <paramref name="fullStderr"/>, when supplied, accumulates the whole stderr
    // stream (capped) — the audio analysis pass reads its loudnorm JSON / astats
    // numbers from there. Otherwise only a 20-line tail is kept for errors.
    internal static bool RunFfmpeg(string args, double totalSec, Action<double>? progress,
                                   CancellationToken ct, StringBuilder? fullStderr, out string error)
    {
        error = "";
        var exe = FfmpegPath;
        if (exe == null) { error = "ffmpeg not found."; return false; }

        Process? proc = null;
        var stderrTail = new Queue<string>();
        try
        {
            proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderrTail)
                {
                    stderrTail.Enqueue(e.Data);
                    while (stderrTail.Count > 20) stderrTail.Dequeue();
                    // Cap the full capture so a file that spews decode warnings
                    // can't balloon memory.
                    if (fullStderr != null && fullStderr.Length < 262144)
                        fullStderr.AppendLine(e.Data);
                }
            };

            proc.Start();
            proc.BeginErrorReadLine();

            // Read the -progress key=value stream from stdout.
            var reader = proc.StandardOutput;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (ct.IsCancellationRequested) break;
                if (progress != null && totalSec > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    var val = line.Substring("out_time_us=".Length);
                    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                    {
                        double frac = (us / 1_000_000.0) / totalSec;
                        progress(frac < 0 ? 0 : (frac > 1 ? 1 : frac));
                    }
                }
            }

            if (ct.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                error = "Cancelled.";
                return false;
            }

            proc.WaitForExit();
            if (proc.ExitCode == 0) { progress?.Invoke(1.0); return true; }

            lock (stderrTail) error = string.Join("\n", stderrTail);
            if (string.IsNullOrWhiteSpace(error)) error = "ffmpeg exited with code " + proc.ExitCode + ".";
            return false;
        }
        catch (Exception ex)
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
            error = ex.Message;
            return false;
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
        }
    }
}
