using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RandVideoPlayer.Integrations;

/// <summary>
/// Audition-style audio mastering for a video file, done in place with ffmpeg:
/// peak / loudness normalization plus optional multiband compression, with the
/// VIDEO stream copied untouched. Nothing here talks to libvlc; every entry
/// point blocks the calling thread on a child process, so callers must run them
/// off the UI thread (MainForm uses a background Task, mirroring the cut tool).
///
/// The processing order is fixed and deliberate:
///     high-pass -> compressor -> normalizer -> safety limiter
/// The normalizer runs LAST so it always has the final say on level, which is
/// what makes a batch of files land on the same loudness. That means the
/// measurement pass has to measure the signal as it will be *after* compression,
/// so <see cref="Analyze"/> builds the same pre-chain the apply pass will use.
/// </summary>
public static class AudioFx
{
    public enum NormalizeMode
    {
        None,
        /// <summary>Audition's "Normalize to X dB": a flat gain that puts the sample peak exactly on target.</summary>
        Peak,
        /// <summary>EBU R128 / ITU-R BS.1770 loudness (LUFS). The mode that makes separate files match each other.</summary>
        Loudness,
        /// <summary>Moving-window leveller for material whose level wanders inside the file.</summary>
        Dynamic,
    }

    public enum CompressorStyle
    {
        None,
        /// <summary>4-band, ratio 2, high thresholds. Tightens without being audible.</summary>
        GentleGlue,
        /// <summary>4-band, ratio 3. The "make a quiet track sit with the others" setting.</summary>
        Broadcast,
        /// <summary>4-band tuned around speech, with a rumble filter and a presence lift.</summary>
        Voice,
        /// <summary>4-band with a tight low band and fast highs — impact without squashing.</summary>
        Punchy,
        /// <summary>4-band, ratio 5, low thresholds. Very dense; use on badly-recorded sources.</summary>
        Aggressive,
        /// <summary>One classic bus compressor across the whole spectrum.</summary>
        SingleBand,
    }

    public sealed class Settings
    {
        public NormalizeMode Normalize = NormalizeMode.Loudness;
        /// <summary>Target sample peak in dBFS for <see cref="NormalizeMode.Peak"/>. Audition's default is -0.1.</summary>
        public double PeakTargetDb = -0.1;
        public double LoudnessTargetLufs = -16.0;
        public double TruePeakDb = -1.0;
        public double LoudnessRangeLu = 11.0;
        public CompressorStyle Compressor = CompressorStyle.None;
        public bool HighPass;
        public double HighPassHz = 60;
        public bool Limiter = true;
        public double LimiterCeilingDb = -1.0;
        /// <summary>0 = match the source bitrate (floored at 128 kbps).</summary>
        public int AudioBitrateKbps;

        public bool ChangesAnything =>
            Normalize != NormalizeMode.None || Compressor != CompressorStyle.None || HighPass;

        public Settings Clone() => (Settings)MemberwiseClone();
    }

    /// <summary>Measurements of the signal at the point the normalizer will sit (i.e. post-compression).</summary>
    public sealed class Analysis
    {
        public bool Ok;
        public string? Error;
        public double PeakDb = double.NaN;        // sample peak, dBFS
        public double RmsDb = double.NaN;
        public double IntegratedLufs = double.NaN;
        public double TruePeakDb = double.NaN;    // dBTP
        public double LraLu = double.NaN;
        public double ThresholdLufs = double.NaN;
        public double TargetOffsetDb = double.NaN;
        public bool HasLoudness => !double.IsNaN(IntegratedLufs);
        public bool HasPeak => !double.IsNaN(PeakDb);
    }

    // ---- public entry points -------------------------------------------------

    /// <summary>
    /// One pass over the file with the pre-chain applied, tapping astats (sample
    /// peak / RMS) and loudnorm (integrated LUFS, LRA, true peak, threshold).
    /// Decodes only the audio, so it runs at roughly 50-100x realtime.
    /// </summary>
    public static Analysis Analyze(string input, Settings s, double durationSec,
                                   Action<double>? progress, CancellationToken ct)
    {
        var a = new Analysis();
        if (!Ffmpeg.IsAvailable) { a.Error = "ffmpeg not found."; return a; }

        string pre = BuildPreChain(s, "0:a:0", "afxpre");
        string graph = pre + ";[afxpre]"
                     + "astats=measure_perchannel=none:measure_overall=Peak_level+RMS_level,"
                     + "loudnorm=" + LoudnormTargets(s) + ":print_format=json[afxm]";

        string args = "-hide_banner -nostats -i " + Ffmpeg.Quote(input)
                    + " -filter_complex " + Ffmpeg.Quote(graph)
                    + " -map \"[afxm]\" -progress pipe:1 -f null -";

        var stderr = new StringBuilder();
        bool ok = Ffmpeg.RunFfmpeg(args, durationSec, progress, ct, stderr, out string err);
        string text = stderr.ToString();

        ParseAstats(text, a);
        ParseLoudnorm(text, a);

        if (!ok && !a.HasLoudness && !a.HasPeak)
        {
            a.Error = string.IsNullOrWhiteSpace(err) ? "Analysis failed." : err;
            return a;
        }
        a.Ok = a.HasLoudness || a.HasPeak;
        if (!a.Ok) a.Error = "ffmpeg produced no measurements (does this file have an audio track?).";
        return a;
    }

    /// <summary>
    /// Writes <paramref name="output"/> with the processed audio and a stream copy
    /// of everything else. <paramref name="measured"/> supplies the second-pass
    /// numbers; pass an unmeasured Analysis to fall back to single-pass behaviour.
    /// </summary>
    public static bool Apply(string input, string output, Settings s, Analysis? measured,
                             Ffmpeg.MediaInfo? info, double durationSec,
                             Action<double>? progress, CancellationToken ct, out string error)
    {
        if (!Ffmpeg.IsAvailable) { error = "ffmpeg not found."; return false; }
        string graph = BuildFullChain(s, measured, "0:a:0", "afxout");

        // -map 0 keeps video / subtitles / chapters / metadata; -map -0:a drops the
        // source audio so the filtered stream is the only audio in the output.
        string args = "-hide_banner -y -i " + Ffmpeg.Quote(input)
                    + " -filter_complex " + Ffmpeg.Quote(graph)
                    + " -map 0 -map -0:a? -map \"[afxout]\""
                    + " -c copy " + AudioEncoderArgs(output, info, s)
                    + FastStartFor(output)
                    + " -progress pipe:1 -nostats " + Ffmpeg.Quote(output);
        return Ffmpeg.RunFfmpeg(args, durationSec, progress, ct, out error);
    }

    /// <summary>
    /// A short excerpt with the same processing, for A/B listening. Video is
    /// stream-copied from the nearest preceding keyframe, so this is near-instant
    /// even on long files. The loudness numbers come from the WHOLE file so the
    /// excerpt sounds like the finished result rather than like itself normalized.
    /// </summary>
    public static bool RenderPreview(string input, string output, double startSec, double durSec,
                                     Settings s, Analysis? measured, Ffmpeg.MediaInfo? info,
                                     CancellationToken ct, out string error)
    {
        if (!Ffmpeg.IsAvailable) { error = "ffmpeg not found."; return false; }
        string graph = BuildFullChain(s, measured, "0:a:0", "afxout");
        bool hasVideo = info?.HasVideo ?? true;

        string args = "-hide_banner -y -ss " + Ffmpeg.Sec(Math.Max(0, startSec))
                    + " -t " + Ffmpeg.Sec(Math.Max(1, durSec))
                    + " -i " + Ffmpeg.Quote(input)
                    + " -filter_complex " + Ffmpeg.Quote(graph)
                    + (hasVideo ? " -map 0:v:0 -c:v copy" : "")
                    + " -map \"[afxout]\" " + AudioEncoderArgs(output, info, s)
                    + FastStartFor(output)
                    + " -nostats " + Ffmpeg.Quote(output);
        return Ffmpeg.RunFfmpeg(args, durSec, null, ct, out error);
    }

    // ---- descriptions used by the UI ----------------------------------------

    public static string StyleName(CompressorStyle st) => st switch
    {
        CompressorStyle.None => "Off",
        CompressorStyle.GentleGlue => "Gentle glue (4-band)",
        CompressorStyle.Broadcast => "Broadcast / consistent (4-band)",
        CompressorStyle.Voice => "Voice / dialogue (4-band)",
        CompressorStyle.Punchy => "Punchy (4-band)",
        CompressorStyle.Aggressive => "Aggressive / dense (4-band)",
        CompressorStyle.SingleBand => "Single-band bus compressor",
        _ => st.ToString(),
    };

    public static string StyleDescription(CompressorStyle st) => st switch
    {
        CompressorStyle.None => "No dynamics processing — normalization only.",
        CompressorStyle.GentleGlue => "Ratio 2:1 per band, high thresholds. Barely audible.",
        CompressorStyle.Broadcast => "Ratio 3:1 per band — lifts quiet passages up to match.",
        CompressorStyle.Voice => "Low band held down, 500 Hz – 3.5 kHz presence lift.",
        CompressorStyle.Punchy => "Tight low band, fast highs. Keeps transients, adds body.",
        CompressorStyle.Aggressive => "Ratio 5:1, low thresholds. Very dense; for rough sources.",
        CompressorStyle.SingleBand => "One 2.5:1 compressor across the spectrum. Transparent.",
        _ => "",
    };

    // ---- chain construction --------------------------------------------------

    // Everything ahead of the normalizer. Kept separate because Analyze has to
    // measure at exactly this point in the signal path.
    private static string BuildPreChain(Settings s, string inLabel, string outLabel)
    {
        var sb = new StringBuilder();
        string cur = inLabel;
        int n = 0;
        string Next() => "afx" + (++n);

        if (s.HighPass)
        {
            string o = Next();
            sb.Append('[').Append(cur).Append(']')
              .Append("highpass=f=").Append(Num(Math.Clamp(s.HighPassHz, 20, 300))).Append(":poles=2")
              .Append('[').Append(o).Append("];");
            cur = o;
        }

        var bands = BandsFor(s.Compressor);
        if (bands == null)
        {
            if (s.Compressor == CompressorStyle.SingleBand)
            {
                string o = Next();
                sb.Append('[').Append(cur).Append(']')
                  .Append(Comp(-20, 2.5, 20, 250, 3, 6))
                  .Append('[').Append(o).Append("];");
                cur = o;
            }
        }
        else
        {
            // acrossover=split=A B C yields 4 outputs: <A, A-B, B-C, >C.
            var splits = new List<string>();
            for (int i = 0; i < bands.Count - 1; i++) splits.Add(Num(bands[i].CrossoverHz));
            string[] raw = new string[bands.Count];
            for (int i = 0; i < bands.Count; i++) raw[i] = Next();

            sb.Append('[').Append(cur).Append(']')
              .Append("acrossover=split=").Append(string.Join(" ", splits)).Append(":order=4th");
            foreach (var r in raw) sb.Append('[').Append(r).Append(']');
            sb.Append(';');

            string[] done = new string[bands.Count];
            for (int i = 0; i < bands.Count; i++)
            {
                done[i] = Next();
                var b = bands[i];
                sb.Append('[').Append(raw[i]).Append(']')
                  .Append(Comp(b.ThresholdDb, b.Ratio, b.AttackMs, b.ReleaseMs, b.MakeupDb, b.Knee))
                  .Append('[').Append(done[i]).Append("];");
            }

            string mix = Next();
            foreach (var d in done) sb.Append('[').Append(d).Append(']');
            // normalize=0 is essential: the default divides by the input count and
            // would drop the mix ~12 dB.
            sb.Append("amix=inputs=").Append(bands.Count).Append(":normalize=0:dropout_transition=0")
              .Append('[').Append(mix).Append("];");
            cur = mix;
        }

        // Always land on the requested label so callers can splice onto it.
        sb.Append('[').Append(cur).Append("]anull[").Append(outLabel).Append(']');
        return sb.ToString();
    }

    // Pre-chain + normalizer + safety limiter, ending on outLabel.
    private static string BuildFullChain(Settings s, Analysis? m, string inLabel, string outLabel)
    {
        var sb = new StringBuilder();
        sb.Append(BuildPreChain(s, inLabel, "afxpre"));
        string cur = "afxpre";
        int n = 0;
        string Next() => "afxn" + (++n);

        string? norm = NormalizerFilter(s, m);
        if (norm != null)
        {
            string o = Next();
            sb.Append(";[").Append(cur).Append(']').Append(norm).Append('[').Append(o).Append(']');
            cur = o;
        }

        if (s.Limiter)
        {
            string o = Next();
            // The limiter must never sit BELOW what the normalizer was asked to
            // hit, or it quietly overrides the target the user typed: a peak
            // target of -0.1 dBFS with a -1.0 dBFS ceiling would land on -1.0.
            // Raising it to the target keeps it a genuine safety net (a no-op
            // when the normalizer is already exact) instead of a second opinion.
            double ceilingDb = s.Normalize switch
            {
                NormalizeMode.Peak => Math.Max(s.LimiterCeilingDb, s.PeakTargetDb),
                NormalizeMode.Loudness => Math.Max(s.LimiterCeilingDb, s.TruePeakDb),
                _ => s.LimiterCeilingDb,
            };
            double limit = Math.Clamp(FromDb(Math.Min(0, ceilingDb)), 0.0625, 1.0);
            sb.Append(";[").Append(cur).Append(']')
              .Append("alimiter=limit=").Append(Num(limit)).Append(":level=false:attack=5:release=50")
              .Append('[').Append(o).Append(']');
            cur = o;
        }

        sb.Append(";[").Append(cur).Append("]anull[").Append(outLabel).Append(']');
        return sb.ToString();
    }

    private static string? NormalizerFilter(Settings s, Analysis? m)
    {
        switch (s.Normalize)
        {
            case NormalizeMode.Peak:
            {
                if (m == null || !m.HasPeak) return null;
                double gain = Math.Clamp(s.PeakTargetDb - m.PeakDb, -60, 60);
                return "volume=" + Num(gain) + "dB";
            }
            case NormalizeMode.Loudness:
            {
                var f = new StringBuilder("loudnorm=").Append(LoudnormTargets(s));
                if (m != null && m.HasLoudness)
                {
                    // Second pass. Out-of-range measurements make loudnorm bail, so
                    // clamp to the ranges the filter documents.
                    f.Append(":measured_I=").Append(Num(Math.Clamp(m.IntegratedLufs, -99, 0)))
                     .Append(":measured_TP=").Append(Num(Math.Clamp(m.TruePeakDb, -99, 99)))
                     .Append(":measured_LRA=").Append(Num(Math.Clamp(m.LraLu, 0, 99)))
                     .Append(":measured_thresh=").Append(Num(Math.Clamp(m.ThresholdLufs, -99, 0)));
                    if (!double.IsNaN(m.TargetOffsetDb))
                        f.Append(":offset=").Append(Num(Math.Clamp(m.TargetOffsetDb, -99, 99)));
                    f.Append(":linear=true");
                }
                return f.ToString();
            }
            case NormalizeMode.Dynamic:
            {
                double peak = Math.Clamp(FromDb(s.LimiterCeilingDb), 0.1, 0.99);
                return "dynaudnorm=framelen=500:gausssize=31:peak=" + Num(peak)
                     + ":maxgain=10:compress=6:altboundary=true";
            }
            default:
                return null;
        }
    }

    private static string LoudnormTargets(Settings s)
        => "I=" + Num(Math.Clamp(s.LoudnessTargetLufs, -70, -5))
         + ":TP=" + Num(Math.Clamp(s.TruePeakDb, -9, 0))
         + ":LRA=" + Num(Math.Clamp(s.LoudnessRangeLu, 1, 50));

    // acompressor takes LINEAR threshold (0.000976563-1) and makeup (1-64), so the
    // dB values the presets are written in get converted here rather than relying
    // on ffmpeg's dB-suffix parsing.
    private static string Comp(double thresholdDb, double ratio, double attackMs, double releaseMs,
                               double makeupDb, double knee)
        => "acompressor="
         + "threshold=" + Num(Math.Clamp(FromDb(thresholdDb), 0.000976563, 1))
         + ":ratio=" + Num(Math.Clamp(ratio, 1, 20))
         + ":attack=" + Num(Math.Clamp(attackMs, 0.01, 2000))
         + ":release=" + Num(Math.Clamp(releaseMs, 0.01, 9000))
         + ":makeup=" + Num(Math.Clamp(FromDb(makeupDb), 1, 64))
         + ":knee=" + Num(Math.Clamp(knee, 1, 8))
         + ":detection=rms:link=average";

    private readonly struct Band
    {
        public readonly double CrossoverHz;   // upper edge; ignored on the last band
        public readonly double ThresholdDb, Ratio, AttackMs, ReleaseMs, MakeupDb, Knee;
        public Band(double hz, double thr, double ratio, double atk, double rel, double makeup, double knee)
        { CrossoverHz = hz; ThresholdDb = thr; Ratio = ratio; AttackMs = atk; ReleaseMs = rel; MakeupDb = makeup; Knee = knee; }
    }

    // Null = not a multiband style. Crossovers follow Audition's multiband defaults
    // (roughly 120 / 720 / 4800 Hz), moved per style where the material wants it.
    private static List<Band>? BandsFor(CompressorStyle st) => st switch
    {
        CompressorStyle.GentleGlue => new List<Band>
        {
            new(120,  -18, 2.0, 30, 300, 1.5, 6),
            new(720,  -18, 2.0, 20, 220, 1.5, 6),
            new(4800, -18, 2.0, 12, 160, 1.5, 6),
            new(0,    -20, 2.0,  5, 110, 1.5, 6),
        },
        CompressorStyle.Broadcast => new List<Band>
        {
            new(120,  -24, 3.0, 25, 250, 4.0, 4),
            new(720,  -24, 3.0, 15, 180, 4.0, 4),
            new(4800, -24, 3.0,  8, 140, 4.0, 4),
            new(0,    -26, 3.0,  3,  90, 4.0, 4),
        },
        CompressorStyle.Voice => new List<Band>
        {
            // Low band held down hard (rumble / plosives), mids lifted for intelligibility.
            new(90,   -34, 4.0, 20, 220, 0.0, 3),
            new(500,  -26, 2.5, 15, 180, 3.0, 4),
            new(3500, -26, 3.5, 10, 140, 5.0, 4),
            new(0,    -28, 2.5,  4, 100, 2.0, 4),
        },
        CompressorStyle.Punchy => new List<Band>
        {
            new(120,  -22, 3.5, 35, 260, 3.0, 3),
            new(900,  -22, 2.0, 25, 200, 2.5, 5),
            new(5000, -22, 3.0, 10, 150, 3.0, 4),
            new(0,    -24, 2.0,  3,  80, 2.0, 5),
        },
        CompressorStyle.Aggressive => new List<Band>
        {
            new(100,  -30, 5.0, 15, 200, 6.0, 2),
            new(800,  -30, 5.0, 10, 150, 6.0, 2),
            new(5000, -30, 5.0,  5, 110, 6.0, 2),
            new(0,    -32, 5.0,  2,  70, 6.0, 2),
        },
        _ => null,
    };

    // ---- encoder selection ---------------------------------------------------

    // The container decides the codec: an mp4 can't hold opus-in-webm and an mp3
    // can't hold aac. Everything in this library is mp4/aac, but the audio-only
    // and webm strays have to survive the round trip too.
    private static string AudioEncoderArgs(string outputPath, Ffmpeg.MediaInfo? info, Settings s)
    {
        string ext = (Path.GetExtension(outputPath) ?? "").ToLowerInvariant();
        // The temp file carries the source extension through an ".rvpaudio-tmp" infix.
        int srcKbps = info?.AudioBitrateKbps ?? 0;
        int bitrate = s.AudioBitrateKbps > 0
            ? s.AudioBitrateKbps
            : (srcKbps > 0 ? Math.Clamp(srcKbps, 128, 320) : 192);

        string src = (info?.AudioCodec ?? "").ToLowerInvariant();

        switch (ext)
        {
            case ".webm":
            case ".opus":
                // libopus always writes 48 kHz and resamples anything else itself.
                return "-c:a libopus -b:a " + bitrate + "k";
            case ".ogg":
                // Ogg takes either; keep whichever the file already used.
                return src == "opus"
                    ? "-c:a libopus -b:a " + bitrate + "k"
                    : "-c:a libvorbis -b:a " + bitrate + "k" + RateCap(info, 48000);
            case ".mp3":
                return "-c:a libmp3lame -b:a " + bitrate + "k" + RateCap(info, 48000);
            case ".flac":
                return "-c:a flac";
            case ".wav":
                // Preserve the sample format rather than flattening 24-bit or
                // float masters down to 16-bit on the way through.
                return src.StartsWith("pcm_", StringComparison.Ordinal) ? "-c:a " + src : "-c:a pcm_s16le";
            default:
                return "-c:a aac -b:a " + bitrate + "k" + RateCap(info, 96000);
        }
    }

    // Some encoders simply refuse rates the source may carry: libvorbis fails
    // outright at 96 kHz, lame tops out at 48 kHz. Resampling down is inaudible
    // and is the only way to write that container at all, so cap rather than
    // fail. Encoders that handle any rate (or resample internally, like opus)
    // get no -ar and keep the source rate.
    private static string RateCap(Ffmpeg.MediaInfo? info, int maxHz)
    {
        int rate = info?.SampleRate ?? 0;
        return rate > maxHz ? " -ar " + maxHz : "";
    }

    private static string FastStartFor(string outputPath)
    {
        string ext = (Path.GetExtension(outputPath) ?? "").ToLowerInvariant();
        // -movflags belongs to the mov/mp4 muxer; other muxers reject it outright.
        return ext is ".mp4" or ".m4v" or ".mov" or ".m4a" ? " -movflags +faststart" : "";
    }

    // ---- measurement parsing -------------------------------------------------

    private static void ParseAstats(string stderr, Analysis a)
    {
        foreach (var raw in stderr.Split('\n'))
        {
            int i = raw.IndexOf("Peak level dB:", StringComparison.Ordinal);
            if (i >= 0 && double.IsNaN(a.PeakDb)
                && double.TryParse(raw.AsSpan(i + 14).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pk))
                a.PeakDb = pk;

            int j = raw.IndexOf("RMS level dB:", StringComparison.Ordinal);
            if (j >= 0 && double.IsNaN(a.RmsDb)
                && double.TryParse(raw.AsSpan(j + 13).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rms))
                a.RmsDb = rms;
        }
    }

    // loudnorm's JSON block is printed to stderr surrounded by ffmpeg log lines,
    // so pull out the brace-delimited object rather than parsing the whole stream.
    private static void ParseLoudnorm(string stderr, Analysis a)
    {
        int start = stderr.LastIndexOf("\"input_i\"", StringComparison.Ordinal);
        if (start < 0) return;
        int open = stderr.LastIndexOf('{', start);
        int close = stderr.IndexOf('}', start);
        if (open < 0 || close < 0 || close <= open) return;

        try
        {
            using var doc = JsonDocument.Parse(stderr.Substring(open, close - open + 1));
            var r = doc.RootElement;
            a.IntegratedLufs = Get(r, "input_i");
            a.TruePeakDb = Get(r, "input_tp");
            a.LraLu = Get(r, "input_lra");
            a.ThresholdLufs = Get(r, "input_thresh");
            a.TargetOffsetDb = Get(r, "target_offset");
        }
        catch { /* malformed block — the astats numbers may still be usable */ }

        static double Get(JsonElement r, string name)
            => r.TryGetProperty(name, out var v)
               && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
               && !double.IsInfinity(d)
                ? d : double.NaN;
    }

    // ---- small helpers -------------------------------------------------------

    public static double FromDb(double db) => Math.Pow(10.0, db / 20.0);
    public static double ToDb(double linear) => linear <= 0 ? -120 : 20.0 * Math.Log10(linear);

    private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
