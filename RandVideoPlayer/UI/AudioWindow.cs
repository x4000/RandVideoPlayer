using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RandVideoPlayer.Integrations;

namespace RandVideoPlayer.UI;

/// <summary>
/// What the user asked for when they pressed Apply or Preview. MainForm owns the
/// ffmpeg run and the file swap, so the window hands over a snapshot rather than
/// a live reference to its own state.
/// </summary>
public sealed class AudioRequest
{
    public AudioFx.Settings Settings = new();
    public AudioFx.Analysis? Measured;
    public long StartMs;
}

/// <summary>
/// Pop-out audio mastering tool — the in-app replacement for round-tripping a
/// file through Audition. Collects a normalization target and a compression
/// style, shows what the file currently measures, and predicts where it will
/// land. It owns the (read-only) analysis pass itself; anything that touches
/// playback or the file on disk is raised to MainForm.
///
/// The analysis is deliberately re-run whenever the compressor or the rumble
/// filter changes, because the normalizer sits AFTER them in the chain and so
/// its input is a different signal each time.
/// </summary>
public sealed class AudioWindow : Form, IMediaJobUi
{
    private readonly string _path;
    private readonly double _durationSec;
    private readonly Ffmpeg.MediaInfo? _info;
    private readonly Func<long> _getTimeMs;

    private Theme _theme;
    private bool _busy;
    private bool _previewing;
    private bool _suppressEvents;

    private AudioFx.Analysis? _measured;
    // Identifies the settings the most recently STARTED analysis was run with, so
    // an edit that changes what gets measured can invalidate it.
    private string? _analysisKey;
    private CancellationTokenSource? _analysisCts;
    private readonly System.Windows.Forms.Timer _analyzeDebounce = new() { Interval = 600 };

    private readonly Label _fileLabel = new();
    private readonly Label _measuredLabel = new();
    private readonly Button _analyze = new();

    private readonly ComboBox _compressor = new();
    private readonly Label _compDesc = new();

    private readonly ComboBox _normMode = new();
    private readonly Label _peakCaption = new();
    private readonly TextBox _peakValue = new();
    private readonly ComboBox _peakUnit = new();
    private readonly Label _lufsCaption = new();
    private readonly TextBox _lufs = new();
    private readonly Label _tpCaption = new();
    private readonly TextBox _tp = new();
    private readonly Label _lraCaption = new();
    private readonly TextBox _lra = new();

    private readonly CheckBox _limiter = new();
    private readonly TextBox _ceiling = new();
    private readonly CheckBox _highPass = new();
    private readonly Label _bitrateCaption = new();
    private readonly ComboBox _bitrate = new();

    private readonly Label _predict = new();
    private readonly Button _previewProcessed = new();
    private readonly Button _previewOriginal = new();
    private readonly Button _apply = new();
    private readonly Button _close = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();

    /// <summary>Apply the chain to the file and replace it (MainForm does the swap).</summary>
    public event Action<AudioRequest>? ApplyConfirmed;
    /// <summary>Render a short processed excerpt and play it in the main window.</summary>
    public event Action<AudioRequest>? PreviewRequested;
    /// <summary>Drop back to the untouched file at the position the preview started from.</summary>
    public event Action? PreviewStopRequested;
    /// <summary>Raised when the settings change so MainForm can persist them as the new defaults.</summary>
    public event Action<AudioFx.Settings>? SettingsChanged;

    public AudioWindow(string path, string fileName, Theme theme, AudioFx.Settings initial,
                       Ffmpeg.MediaInfo? info, Func<long> getTimeMs)
    {
        _path = path;
        _theme = theme;
        _info = info;
        _durationSec = info?.DurationSec ?? 0;
        _getTimeMs = getTimeMs;

        Text = "Audio — " + fileName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 368);

        _fileLabel.Text = fileName;
        _fileLabel.AutoEllipsis = true;
        _fileLabel.Dock = DockStyle.Fill;
        _fileLabel.Padding = new Padding(12, 4, 12, 0);
        _fileLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

        // ---- measured row ----
        _measuredLabel.AutoSize = false;
        _measuredLabel.Width = 540;
        _measuredLabel.Margin = new Padding(0, 4, 8, 0);
        _measuredLabel.Font = new Font("Consolas", 9f);
        _analyze.Text = "Re-analyze";
        StyleButton(_analyze);
        _analyze.Click += (_, __) => StartAnalysis();
        var measuredRow = Row(new Control[] { Caption("Measured:", 76), _measuredLabel }, 12, 2);
        measuredRow.Controls.Add(_analyze);

        // ---- compressor row ----
        _compressor.DropDownStyle = ComboBoxStyle.DropDownList;
        _compressor.Width = 260;
        foreach (AudioFx.CompressorStyle st in Enum.GetValues<AudioFx.CompressorStyle>())
            _compressor.Items.Add(new StyleItem(st));
        _compressor.SelectedIndexChanged += (_, __) => { OnPreChainChanged(); };
        _compDesc.AutoSize = false;
        _compDesc.Width = 380;
        _compDesc.Margin = new Padding(8, 5, 0, 0);
        _compDesc.Font = new Font("Segoe UI", 8.5f);
        var compRow = Row(new Control[] { Caption("Compression:", 96), _compressor, _compDesc }, 12, 2);

        // ---- normalization rows ----
        _normMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _normMode.Width = 260;
        _normMode.Items.AddRange(new object[]
        {
            "None",
            "Peak  (Audition-style)",
            "Loudness  (LUFS / EBU R128)",
            "Dynamic leveller",
        });
        _normMode.SelectedIndexChanged += (_, __) => { SyncEnabled(); PushSettings(); };
        var normRow = Row(new Control[] { Caption("Normalize:", 96), _normMode }, 12, 2);

        _peakCaption.Text = "Normalize to";
        _peakCaption.AutoSize = true;
        _peakCaption.Padding = new Padding(0, 4, 0, 0);
        _peakValue.Width = 70;
        _peakValue.TextAlign = HorizontalAlignment.Right;
        _peakUnit.DropDownStyle = ComboBoxStyle.DropDownList;
        _peakUnit.Width = 190;
        _peakUnit.Items.AddRange(new object[] { "dB below full scale", "% of full scale" });
        var peakRow = Row(new Control[] { Spacer(96), _peakCaption, _peakValue, _peakUnit }, 12, 2);

        _lufsCaption.Text = "Target";
        _tpCaption.Text = "True peak";
        _lraCaption.Text = "Range";
        foreach (var l in new[] { _lufsCaption, _tpCaption, _lraCaption })
        {
            l.AutoSize = true;
            l.Padding = new Padding(0, 4, 0, 0);
            l.Margin = new Padding(8, 0, 4, 0);
        }
        foreach (var t in new[] { _lufs, _tp, _lra })
        {
            t.Width = 58;
            t.TextAlign = HorizontalAlignment.Right;
        }
        var lufsRow = Row(new Control[]
        {
            Spacer(96), _lufsCaption, _lufs, Unit("LUFS"),
            _tpCaption, _tp, Unit("dBTP"),
            _lraCaption, _lra, Unit("LU"),
        }, 12, 2);

        // ---- options row ----
        _limiter.Text = "Safety limiter at";
        _limiter.AutoSize = true;
        _limiter.Margin = new Padding(0, 3, 4, 0);
        _ceiling.Width = 58;
        _ceiling.TextAlign = HorizontalAlignment.Right;
        _highPass.Text = "Rumble filter (60 Hz)";
        _highPass.AutoSize = true;
        _highPass.Margin = new Padding(16, 3, 0, 0);
        _highPass.CheckedChanged += (_, __) => OnPreChainChanged();
        _bitrateCaption.Text = "Audio bitrate";
        _bitrateCaption.AutoSize = true;
        _bitrateCaption.Margin = new Padding(16, 4, 4, 0);
        _bitrate.DropDownStyle = ComboBoxStyle.DropDownList;
        _bitrate.Width = 116;
        _bitrate.Items.AddRange(new object[] { "Match source", "160 kbps", "192 kbps", "256 kbps", "320 kbps" });
        var optRow = Row(new Control[]
        {
            Spacer(96), _limiter, _ceiling, Unit("dBFS"), _highPass, _bitrateCaption, _bitrate,
        }, 12, 2);

        // ---- prediction ----
        _predict.Dock = DockStyle.Fill;
        _predict.Padding = new Padding(12, 4, 12, 2);
        _predict.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

        // ---- preview + actions ----
        _previewProcessed.Text = "▶ Preview 20 s (processed)";
        _previewOriginal.Text = "◼ Back to original";
        _apply.Text = "Apply && Save";
        _close.Text = "Close";
        foreach (var b in new[] { _previewProcessed, _previewOriginal, _apply, _close }) StyleButton(b);
        _apply.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _previewProcessed.Click += (_, __) => RaisePreview();
        _previewOriginal.Click += (_, __) => { _previewing = false; SyncEnabled(); PreviewStopRequested?.Invoke(); };
        _apply.Click += (_, __) => RaiseApply();
        _close.Click += (_, __) => { if (!_busy) Close(); };
        var previewRow = Row(new Control[] { _previewProcessed, _previewOriginal }, 12, 4);
        var actionRow = Row(new Control[] { _apply, Spacer(8), _close }, 12, 4);

        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(12, 2, 12, 2);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 1000;
        _progress.Visible = false;

        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(12, 2, 12, 2);
        _status.Font = new Font("Segoe UI", 8.5f);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 12 };
        int[] heights = { 24, 30, 30, 30, 30, 30, 30, 44, 38, 40, 18, 22 };
        foreach (var h in heights) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        root.Controls.Add(_fileLabel, 0, 0);
        root.Controls.Add(measuredRow, 0, 1);
        root.Controls.Add(compRow, 0, 2);
        root.Controls.Add(normRow, 0, 3);
        root.Controls.Add(peakRow, 0, 4);
        root.Controls.Add(lufsRow, 0, 5);
        root.Controls.Add(optRow, 0, 6);
        root.Controls.Add(_predict, 0, 7);
        root.Controls.Add(previewRow, 0, 8);
        root.Controls.Add(actionRow, 0, 9);
        root.Controls.Add(_progress, 0, 10);
        root.Controls.Add(_status, 0, 11);
        Controls.Add(root);

        LoadSettings(initial);

        // Text boxes only take effect on leave / Enter so half-typed numbers
        // ("-" on the way to "-16") never reach the chain builder.
        foreach (var t in new[] { _peakValue, _lufs, _tp, _lra, _ceiling })
        {
            t.Leave += (_, __) => PushSettings();
            t.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Enter) { e2.SuppressKeyPress = true; PushSettings(); } };
        }
        _peakUnit.SelectedIndexChanged += (_, __) => { if (!_suppressEvents) { RewritePeakForUnit(); PushSettings(); } };
        _limiter.CheckedChanged += (_, __) => PushSettings();
        _bitrate.SelectedIndexChanged += (_, __) => PushSettings();

        _analyzeDebounce.Tick += (_, __) => { _analyzeDebounce.Stop(); StartAnalysis(); };

        ApplyTheme(theme);
        SyncEnabled();
    }

    // ---- construction helpers ------------------------------------------------

    private static Control Spacer(int w) => new Panel { Width = w, Height = 1, Margin = new Padding(0) };

    private static Label Caption(string text, int width) => new()
    {
        Text = text,
        Width = width,
        AutoSize = false,
        Margin = new Padding(0, 4, 0, 0),
    };

    private static Label Unit(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3, 4, 6, 0),
    };

    private static FlowLayoutPanel Row(Control[] controls, int padX, int padY)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false, Padding = new Padding(padX, padY, padX, padY) };
        p.Controls.AddRange(controls);
        return p;
    }

    private void StyleButton(Button b)
    {
        b.AutoSize = true;
        b.FlatStyle = FlatStyle.Flat;
        b.Margin = new Padding(0, 0, 8, 0);
    }

    private sealed class StyleItem
    {
        public readonly AudioFx.CompressorStyle Style;
        public StyleItem(AudioFx.CompressorStyle s) => Style = s;
        public override string ToString() => AudioFx.StyleName(Style);
    }

    // ---- settings <-> controls ----------------------------------------------

    private void LoadSettings(AudioFx.Settings s)
    {
        _suppressEvents = true;
        try
        {
            for (int i = 0; i < _compressor.Items.Count; i++)
                if (_compressor.Items[i] is StyleItem it && it.Style == s.Compressor) { _compressor.SelectedIndex = i; break; }
            if (_compressor.SelectedIndex < 0) _compressor.SelectedIndex = 0;

            _normMode.SelectedIndex = (int)s.Normalize;
            _peakUnit.SelectedIndex = 0;
            _peakValue.Text = Fmt(s.PeakTargetDb);
            _lufs.Text = Fmt(s.LoudnessTargetLufs);
            _tp.Text = Fmt(s.TruePeakDb);
            _lra.Text = Fmt(s.LoudnessRangeLu);
            _limiter.Checked = s.Limiter;
            _ceiling.Text = Fmt(s.LimiterCeilingDb);
            _highPass.Checked = s.HighPass;
            _bitrate.SelectedIndex = s.AudioBitrateKbps switch
            {
                160 => 1, 192 => 2, 256 => 3, 320 => 4, _ => 0,
            };
        }
        finally { _suppressEvents = false; }
        SyncEnabled();
    }

    private AudioFx.Settings ReadSettings()
    {
        var s = new AudioFx.Settings
        {
            Normalize = (AudioFx.NormalizeMode)Math.Clamp(_normMode.SelectedIndex, 0, 3),
            Compressor = _compressor.SelectedItem is StyleItem it ? it.Style : AudioFx.CompressorStyle.None,
            HighPass = _highPass.Checked,
            Limiter = _limiter.Checked,
            LimiterCeilingDb = Math.Clamp(ParseDb(_ceiling.Text, -1.0), -24, 0),
            LoudnessTargetLufs = Math.Clamp(Parse(_lufs.Text, -16.0), -70, -5),
            TruePeakDb = Math.Clamp(Parse(_tp.Text, -1.0), -9, 0),
            LoudnessRangeLu = Math.Clamp(Parse(_lra.Text, 11.0), 1, 50),
            AudioBitrateKbps = _bitrate.SelectedIndex switch { 1 => 160, 2 => 192, 3 => 256, 4 => 320, _ => 0 },
        };

        if (_peakUnit.SelectedIndex == 1)
        {
            // Percentage of full scale, the way Audition offers it: 100% = 0 dBFS.
            double pct = Math.Clamp(Parse(_peakValue.Text, 100.0), 0.0001, 100.0);
            s.PeakTargetDb = AudioFx.ToDb(pct / 100.0);
        }
        else
        {
            s.PeakTargetDb = Math.Clamp(ParseDb(_peakValue.Text, -0.1), -60, 0);
        }
        return s;
    }

    // "Normalize to 0.1 dB" in Audition means 0.1 dB BELOW full scale, so an
    // unsigned entry is read as a headroom figure rather than as +0.1 dBFS
    // (which would just clip). A typed minus sign is honoured as-is.
    private static double ParseDb(string text, double fallback)
    {
        double v = Parse(text, fallback);
        return v > 0 ? -v : v;
    }

    private static double Parse(string text, double fallback)
        => double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void RewritePeakForUnit()
    {
        // Keep the same physical target when the user flips dB <-> %.
        _suppressEvents = true;
        try
        {
            if (_peakUnit.SelectedIndex == 1)
            {
                double db = Math.Clamp(ParseDb(_peakValue.Text, -0.1), -60, 0);
                _peakValue.Text = (AudioFx.FromDb(db) * 100.0).ToString("0.####", CultureInfo.InvariantCulture);
            }
            else
            {
                double pct = Math.Clamp(Parse(_peakValue.Text, 100.0), 0.0001, 100.0);
                _peakValue.Text = Fmt(AudioFx.ToDb(pct / 100.0));
            }
        }
        finally { _suppressEvents = false; }
    }

    private void PushSettings()
    {
        if (_suppressEvents) return;
        var s = ReadSettings();
        SettingsChanged?.Invoke(s);
        if (_analysisKey != null && _analysisKey != AnalysisKey(s))
        {
            _measured = null;
            _analyzeDebounce.Stop();
            _analyzeDebounce.Start();
        }
        SyncPrediction();
    }

    // Everything the measurement pass depends on. The compressor and the rumble
    // filter sit AHEAD of the normalizer, so they change the signal being
    // measured; loudnorm's targets change the target_offset correction it hands
    // back. Anything else (peak target, limiter, bitrate) leaves the numbers
    // valid, so editing it must not trigger another pass over the file.
    private static string AnalysisKey(AudioFx.Settings s)
        => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}",
            s.Compressor, s.HighPass, s.HighPassHz,
            s.Normalize == AudioFx.NormalizeMode.Loudness
                ? s.LoudnessTargetLufs + "/" + s.TruePeakDb + "/" + s.LoudnessRangeLu
                : "");

    private void OnPreChainChanged()
    {
        if (_compressor.SelectedItem is StyleItem it) _compDesc.Text = AudioFx.StyleDescription(it.Style);
        if (_suppressEvents) return;
        PushSettings();
    }

    private void SyncEnabled()
    {
        var mode = (AudioFx.NormalizeMode)Math.Clamp(_normMode.SelectedIndex, 0, 3);
        bool peak = mode == AudioFx.NormalizeMode.Peak;
        bool loud = mode == AudioFx.NormalizeMode.Loudness;
        bool live = !_busy;

        _peakCaption.Enabled = _peakValue.Enabled = _peakUnit.Enabled = live && peak;
        _lufsCaption.Enabled = _lufs.Enabled = live && loud;
        _tpCaption.Enabled = _tp.Enabled = live && loud;
        _lraCaption.Enabled = _lra.Enabled = live && loud;
        _ceiling.Enabled = live && _limiter.Checked;

        _normMode.Enabled = _compressor.Enabled = _limiter.Enabled = _highPass.Enabled = _bitrate.Enabled = live;
        _analyze.Enabled = live;
        _previewProcessed.Enabled = live && Ffmpeg.IsAvailable;
        _previewOriginal.Enabled = live && _previewing;
        _close.Enabled = live;
        _apply.Enabled = live && ReadSettings().ChangesAnything;
        SyncPrediction();
    }

    // ---- analysis ------------------------------------------------------------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        DarkChrome.ApplyTitleBar(Handle, _theme.IsDark);
        if (_compressor.SelectedItem is StyleItem it) _compDesc.Text = AudioFx.StyleDescription(it.Style);
        StartAnalysis();
    }

    private void StartAnalysis()
    {
        if (_busy || !Ffmpeg.IsAvailable) return;
        _analyzeDebounce.Stop();
        try { _analysisCts?.Cancel(); } catch { }
        _analysisCts?.Dispose();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;

        var settings = ReadSettings();
        _analysisKey = AnalysisKey(settings);
        // Capture the token now: the next keystroke can cancel AND dispose this
        // source while the task is still running, and cts.Token throws once
        // disposed.
        var token = cts.Token;
        _measuredLabel.Text = "analyzing…";
        _measuredLabel.ForeColor = _theme.TextMuted;
        _analyze.Enabled = false;

        Task.Run(() =>
        {
            var a = AudioFx.Analyze(_path, settings, _durationSec, null, token);
            PostToUi(() =>
            {
                // A newer run has already superseded this one.
                if (!ReferenceEquals(_analysisCts, cts)) return;
                _analyze.Enabled = !_busy;
                if (token.IsCancellationRequested) return;
                _measured = a.Ok ? a : null;
                SyncMeasured(a);
                SyncPrediction();
            });
        });
    }

    private void SyncMeasured(AudioFx.Analysis a)
    {
        if (!a.Ok)
        {
            _measuredLabel.ForeColor = _theme.ErrorHighlight;
            _measuredLabel.Text = a.Error ?? "analysis failed";
            return;
        }
        _measuredLabel.ForeColor = _theme.Text;
        _measuredLabel.Text = string.Format(CultureInfo.InvariantCulture,
            "{0,7} LUFS   peak {1,6} dBFS   true peak {2,6} dBTP   range {3,5} LU",
            Show(a.IntegratedLufs), Show(a.PeakDb), Show(a.TruePeakDb), Show(a.LraLu));

        static string Show(double v) => double.IsNaN(v) ? "  —  " : v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void SyncPrediction()
    {
        var s = ReadSettings();
        _predict.ForeColor = _theme.Accent;

        if (!s.ChangesAnything) { _predict.Text = "Nothing selected — pick a normalization mode or a compression style."; return; }
        if (_measured == null) { _predict.Text = "Waiting for analysis…"; return; }

        string comp = s.Compressor == AudioFx.CompressorStyle.None ? "" : AudioFx.StyleName(s.Compressor) + " → ";
        switch (s.Normalize)
        {
            case AudioFx.NormalizeMode.Peak when _measured.HasPeak:
            {
                double gain = s.PeakTargetDb - _measured.PeakDb;
                // The audio is re-encoded, and a lossy encoder's decoded output can
                // sit up to ~1 dB above what went in — worth saying when the target
                // leaves no room for that.
                string headroom = s.PeakTargetDb > -1.0
                    ? "  (the re-encode can overshoot ~1 dB — -1 dBFS leaves room for it)"
                    : "";
                _predict.Text = comp + string.Format(CultureInfo.InvariantCulture,
                    "peak normalize {0:+0.0;-0.0;0.0} dB → peak at {1:0.0} dBFS.{2}", gain, s.PeakTargetDb, headroom);
                break;
            }
            case AudioFx.NormalizeMode.Loudness when _measured.HasLoudness:
            {
                double gain = s.LoudnessTargetLufs - _measured.IntegratedLufs;
                _predict.Text = comp + string.Format(CultureInfo.InvariantCulture,
                    "loudness normalize {0:+0.0;-0.0;0.0} dB → {1:0.0} LUFS, true peak ≤ {2:0.0} dBTP.",
                    gain, s.LoudnessTargetLufs, s.TruePeakDb);
                break;
            }
            case AudioFx.NormalizeMode.Dynamic:
                _predict.Text = comp + "moving-window leveller, peaks held at " + Fmt(s.LimiterCeilingDb) + " dBFS.";
                break;
            case AudioFx.NormalizeMode.None:
                _predict.Text = comp.Length > 0 ? comp.TrimEnd(' ', '→') + " only (level left as-is)." : "";
                break;
            default:
                _predict.Text = comp + "waiting for analysis…";
                break;
        }
    }

    // ---- actions -------------------------------------------------------------

    private AudioRequest BuildRequest() => new()
    {
        Settings = ReadSettings(),
        Measured = _measured,
        StartMs = _getTimeMs(),
    };

    private void RaisePreview()
    {
        if (_busy) return;
        var req = BuildRequest();
        if (!req.Settings.ChangesAnything) { _status.Text = "Nothing to preview — no processing selected."; return; }
        // Peak and loudness normalization are driven by the measurement; without
        // it the excerpt would play back with the compression but no level
        // change, which is not what the buttons promise.
        bool needsMeasurement = req.Settings.Normalize is AudioFx.NormalizeMode.Peak or AudioFx.NormalizeMode.Loudness;
        if (needsMeasurement && req.Measured == null)
        {
            _status.ForeColor = _theme.TextMuted;
            _status.Text = "Still analyzing — try the preview again in a moment.";
            return;
        }
        _status.ForeColor = _theme.Text;
        _previewing = true;
        SyncEnabled();
        PreviewRequested?.Invoke(req);
    }

    private void RaiseApply()
    {
        if (_busy) return;
        var req = BuildRequest();
        if (!req.Settings.ChangesAnything) return;
        ApplyConfirmed?.Invoke(req);
    }

    /// <summary>MainForm calls this when the preview stops on its own (clip ended, or the user moved on).</summary>
    public void NotifyPreviewEnded()
    {
        _previewing = false;
        SyncEnabled();
    }

    // ---- IMediaJobUi ---------------------------------------------------------

    public void PostToUi(Action action)
    {
        try { if (!IsDisposed) BeginInvoke(action); } catch { }
    }

    public void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _progress.Visible = busy;
        if (busy) _progress.Value = 0;
        _status.ForeColor = _theme.Text;
        _status.Text = status;
        SyncEnabled();
    }

    public void ReportProgress(double frac)
    {
        int v = (int)Math.Clamp(frac * 1000, 0, 1000);
        _progress.Value = v;
        _status.Text = "Processing… " + (v / 10) + "%";
    }

    public void ReportDone(bool success, string message)
    {
        SetBusy(false, message);
        _progress.Visible = false;
        if (success)
        {
            // Stay open: the usual workflow is to check the new numbers and, if a
            // track still doesn't sit right, run it again with a different style.
            _status.ForeColor = _theme.Text;
            _measured = null;
            StartAnalysis();
        }
        else
        {
            _status.ForeColor = _theme.ErrorHighlight;
        }
    }

    // ---- theme / lifetime ----------------------------------------------------

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.Panel;
        ForeColor = theme.Text;
        foreach (var c in AllControls(this)) ThemeOne(c, theme);
        SyncPrediction();
        if (IsHandleCreated) DarkChrome.ApplyTitleBar(Handle, theme.IsDark);
    }

    private static System.Collections.Generic.IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var child in AllControls(c)) yield return child;
        }
    }

    private void ThemeOne(Control c, Theme theme)
    {
        switch (c)
        {
            case Button b:
                b.BackColor = theme.ButtonBack;
                b.ForeColor = theme.Text;
                b.FlatAppearance.BorderColor = theme.Border;
                break;
            case TextBox t:
                t.BackColor = theme.ButtonBack;
                t.ForeColor = theme.Text;
                t.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.BackColor = theme.ButtonBack;
                cb.ForeColor = theme.Text;
                cb.FlatStyle = FlatStyle.Flat;
                break;
            case CheckBox ck:
                ck.ForeColor = theme.Text;
                break;
            case Label l:
                l.ForeColor = ReferenceEquals(l, _compDesc) ? theme.TextMuted : theme.Text;
                break;
            default:
                c.BackColor = theme.Panel;
                c.ForeColor = theme.Text;
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        if (_previewing) { _previewing = false; PreviewStopRequested?.Invoke(); }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _analysisCts?.Cancel(); _analysisCts?.Dispose(); } catch { }
            try { _analyzeDebounce.Stop(); _analyzeDebounce.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
