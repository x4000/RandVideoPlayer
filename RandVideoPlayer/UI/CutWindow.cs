using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using RandVideoPlayer.Controls;

namespace RandVideoPlayer.UI;

/// <summary>
/// Payload raised when the user confirms a cut. MainForm performs the actual
/// ffmpeg run and the in-place file swap (it owns playback + file coordination).
/// </summary>
public sealed class CutRequest
{
    public long InMs;
    public long OutMs;
    public bool Reencode;
}

/// <summary>
/// Pop-out trim tool. It does NOT own a video surface — it drives the MAIN
/// player (via the callbacks passed in) so preview scrubbing reuses the app's
/// hardened libvlc pipeline. The window only collects In/Out + mode and hands a
/// <see cref="CutRequest"/> back to MainForm.
/// </summary>
public sealed class CutWindow : Form
{
    private readonly Func<long> _getTimeMs;
    private readonly Func<long> _getLengthMs;
    private readonly Action<long> _seekMs;
    private readonly Action _togglePause;
    private readonly Func<bool> _getIsPlaying;
    private readonly double _frameMs;

    private Theme _theme;
    private List<double> _keyframes = new();
    private bool _busy;
    // Stop-at-Out only fires once playback has been observed BELOW the Out point
    // (cached TimeMs lags ~250ms, so a seek-back would otherwise trip it late).
    private bool _stopArmed;

    private readonly RangeScrubber _range = new() { Dock = DockStyle.Top };
    private readonly FineScrubber _fine = new() { Dock = DockStyle.Fill };
    private readonly Label _fileLabel = new();
    private readonly Label _inLabel = new();
    private readonly Label _outLabel = new();
    private readonly Label _selLabel = new();
    private readonly Label _hintLabel = new();
    private readonly CheckBox _reencode = new();
    private readonly CheckBox _stopAtOut = new();
    private readonly Button _setIn = new();
    private readonly Button _setOut = new();
    private readonly Button _playPause = new();
    private readonly Button _playFromIn = new();
    private readonly Button _previewEnd = new();
    private readonly Button _cut = new();
    private readonly Button _close = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 80 };

    public event Action<CutRequest>? CutConfirmed;

    public CutWindow(string filePath, string fileName, Theme theme, double fps,
                     Func<long> getTimeMs, Func<long> getLengthMs,
                     Action<long> seekMs, Action togglePause, Func<bool> getIsPlaying)
    {
        _theme = theme;
        _getTimeMs = getTimeMs;
        _getLengthMs = getLengthMs;
        _seekMs = seekMs;
        _togglePause = togglePause;
        _getIsPlaying = getIsPlaying;
        _frameMs = fps > 1 ? 1000.0 / fps : 33.0;

        Text = "Cut — " + fileName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 332);
        KeyPreview = true;

        _fileLabel.Text = fileName;
        _fileLabel.AutoEllipsis = true;
        _fileLabel.Dock = DockStyle.Fill;
        _fileLabel.Padding = new Padding(12, 4, 12, 0);
        _fileLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

        _range.Dock = DockStyle.Fill;
        _range.Margin = new Padding(0);
        _fine.Dock = DockStyle.Fill;
        _fine.Margin = new Padding(0);
        _fine.SeekRequested += ms => _seekMs(ms);

        // Time readout row.
        _inLabel.AutoSize = true;
        _outLabel.AutoSize = true;
        _selLabel.AutoSize = true;
        var timeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 2), WrapContents = false, AutoSize = false };
        timeRow.Controls.AddRange(new Control[] { _inLabel, Spacer(24), _outLabel, Spacer(24), _selLabel });

        // Controls row.
        _playPause.Text = "Play / Pause";
        _playFromIn.Text = "▶ From In";
        _previewEnd.Text = "▶ Preview end";
        _setIn.Text = "Set In  [";
        _setOut.Text = "Set Out  ]";
        foreach (var b in new[] { _playPause, _playFromIn, _previewEnd, _setIn, _setOut })
        {
            b.AutoSize = true;
            b.FlatStyle = FlatStyle.Flat;
            b.Margin = new Padding(0, 0, 8, 0);
        }
        _playPause.Click += (_, __) => { if (!_busy) _togglePause(); };
        _playFromIn.Click += (_, __) => { if (!_busy) PlayFrom(_range.InMs); };
        _previewEnd.Click += (_, __) => { if (!_busy) PlayFrom(Math.Max(_range.InMs, _range.OutMs - 3000)); };
        _setIn.Click += (_, __) => { if (_busy) return; _range.InMs = _getTimeMs(); SyncLabels(); };
        _setOut.Click += (_, __) => { if (_busy) return; _range.OutMs = _getTimeMs(); SyncLabels(); };
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4), WrapContents = false, AutoSize = false };
        btnRow.Controls.AddRange(new Control[] { _playPause, _playFromIn, _previewEnd, _setIn, _setOut });

        // Mode row.
        _reencode.Text = "Frame-accurate (re-encodes selection — slight quality loss)";
        _reencode.AutoSize = true;
        _reencode.Margin = new Padding(0, 0, 16, 0);
        _reencode.CheckedChanged += (_, __) => SyncHint();
        _stopAtOut.Text = "Stop at Out point during preview";
        _stopAtOut.AutoSize = true;
        _stopAtOut.Margin = new Padding(0);
        _stopAtOut.Checked = true;
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 2), WrapContents = false, AutoSize = false };
        modeRow.Controls.AddRange(new Control[] { _reencode, _stopAtOut });

        _hintLabel.Dock = DockStyle.Fill;
        _hintLabel.Padding = new Padding(12, 2, 12, 2);
        _hintLabel.Font = new Font("Segoe UI", 8.5f);

        // Action row.
        _cut.Text = "Cut && Save";
        _cut.AutoSize = true;
        _cut.FlatStyle = FlatStyle.Flat;
        _cut.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _cut.Click += OnCutClicked;
        _close.Text = "Close";
        _close.AutoSize = true;
        _close.FlatStyle = FlatStyle.Flat;
        _close.Click += (_, __) => { if (!_busy) Close(); };
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4), WrapContents = false, AutoSize = false };
        actionRow.Controls.AddRange(new Control[] { _cut, Spacer(8), _close });

        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(12, 2, 12, 2);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 1000;
        _progress.Visible = false;

        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(12, 2, 12, 2);
        _status.Font = new Font("Segoe UI", 8.5f);

        // Deterministic vertical stack (avoids Dock=Top z-order ambiguity).
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
        int[] heights = { 24, 52, 54, 26, 40, 26, 22, 40, 18, 22 };
        foreach (var h in heights) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        root.Controls.Add(_fileLabel, 0, 0);
        root.Controls.Add(_range, 0, 1);
        root.Controls.Add(_fine, 0, 2);
        root.Controls.Add(timeRow, 0, 3);
        root.Controls.Add(btnRow, 0, 4);
        root.Controls.Add(modeRow, 0, 5);
        root.Controls.Add(_hintLabel, 0, 6);
        root.Controls.Add(actionRow, 0, 7);
        root.Controls.Add(_progress, 0, 8);
        root.Controls.Add(_status, 0, 9);
        Controls.Add(root);

        _range.InChanged += _ => SyncLabels();
        _range.OutChanged += _ => SyncLabels();
        _range.SeekRequested += ms => _seekMs(ms);

        _tick.Tick += (_, __) => OnTick();
        _tick.Start();

        ApplyTheme(theme);
    }

    private static Control Spacer(int w) => new Panel { Width = w, Height = 1, Margin = new Padding(0) };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        DarkChrome.ApplyTitleBar(Handle, _theme.IsDark);
        long len = _getLengthMs();
        if (len > 0)
        {
            _range.LengthMs = len;
            _range.OutMs = len;
            _fine.LengthMs = len;
        }
        SyncLabels();
    }

    private void OnTick()
    {
        long len = _getLengthMs();
        if (len > 0 && _range.LengthMs != len)
        {
            bool outAtEnd = _range.OutMs == 0 || _range.OutMs == _range.LengthMs;
            _range.LengthMs = len;
            _fine.LengthMs = len;
            if (outAtEnd) _range.OutMs = len;
            SyncLabels();
        }

        long t = _getTimeMs();
        _range.PlayheadMs = t;
        _fine.PlayheadMs = t;

        // Stop-at-Out: pause when playback reaches the Out point so the ending can
        // be previewed. Only armed once we've actually seen the playhead below Out
        // (so a stale, lagging cached time right after a seek-back can't trip it,
        // and playback that deliberately starts past Out isn't halted instantly).
        if (t < _range.OutMs - 30) _stopArmed = true;
        if (_stopAtOut.Checked && !_busy && _stopArmed && _getIsPlaying() && t >= _range.OutMs)
        {
            _stopArmed = false;
            _seekMs(_range.OutMs);
            _togglePause();
        }
    }

    // Seek to a position and make sure playback is running (so From-In / Preview
    // buttons play rather than just repositioning a paused player).
    private void PlayFrom(long ms)
    {
        _stopArmed = false;   // re-arms once the cached playhead drops below Out
        _seekMs(ms);
        if (!_getIsPlaying()) _togglePause();
    }

    /// <summary>Called by MainForm once ffprobe has the keyframe list (background thread → marshaled).</summary>
    public void SetKeyframes(List<double> keyframes)
    {
        _keyframes = keyframes ?? new List<double>();
        SyncHint();
    }

    private void SyncLabels()
    {
        _inLabel.Text = "In: " + Fmt(_range.InMs);
        _outLabel.Text = "Out: " + Fmt(_range.OutMs);
        _selLabel.Text = "Selection: " + Fmt(Math.Max(0, _range.OutMs - _range.InMs));
        _fine.InMs = _range.InMs;
        _fine.OutMs = _range.OutMs;
        _cut.Enabled = !_busy && (_range.OutMs - _range.InMs) >= 200;
        SyncHint();
    }

    private void SyncHint()
    {
        if (_reencode.Checked)
        {
            _hintLabel.ForeColor = _theme.TextMuted;
            _hintLabel.Text = "Frame-accurate: exact In/Out, re-encoded at high quality (CRF 18). Not lossless.";
            return;
        }
        _hintLabel.ForeColor = _theme.TextMuted;
        if (_keyframes.Count == 0)
        {
            _hintLabel.Text = "Lossless: start snaps to nearest keyframe. (Computing keyframes…)";
            return;
        }
        double kf = FfmpegKf(_range.InMs / 1000.0);
        _hintLabel.Text = "Lossless (zero quality loss): start snaps to keyframe at " + Fmt((long)(kf * 1000)) + ".";
    }

    private double FfmpegKf(double sec)
    {
        double best = 0;
        foreach (var k in _keyframes)
        {
            if (k <= sec + 0.0005) best = k; else break;
        }
        return best;
    }

    private static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", (int)ts.TotalHours, ts.Minutes, ts.Seconds, ts.Milliseconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:000}", ts.Minutes, ts.Seconds, ts.Milliseconds);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_busy && !(ActiveControl is TextBox))
        {
            switch (keyData)
            {
                case Keys.Left: Nudge(-_frameMs); return true;
                case Keys.Right: Nudge(_frameMs); return true;
                case Keys.Shift | Keys.Left: Nudge(-1000); return true;
                case Keys.Shift | Keys.Right: Nudge(1000); return true;
                case Keys.I: _range.InMs = _getTimeMs(); SyncLabels(); return true;
                case Keys.O: _range.OutMs = _getTimeMs(); SyncLabels(); return true;
                case Keys.Space: _togglePause(); return true;
                case Keys.Home: _seekMs(_range.InMs); return true;
                case Keys.End: _seekMs(_range.OutMs); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Nudge(double deltaMs)
    {
        long len = _getLengthMs();
        long t = _getTimeMs() + (long)Math.Round(deltaMs);
        _seekMs(Math.Clamp(t, 0, len > 0 ? len : t));
    }

    private void OnCutClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        long inMs = _range.InMs, outMs = _range.OutMs;
        if (outMs - inMs < 200) return;
        CutConfirmed?.Invoke(new CutRequest { InMs = inMs, OutMs = outMs, Reencode = _reencode.Checked });
    }

    // ---- called by MainForm (UI thread) to reflect progress -----------------

    public void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _progress.Visible = busy;
        if (busy) _progress.Value = 0;
        _cut.Enabled = !busy && (_range.OutMs - _range.InMs) >= 200;
        _close.Enabled = !busy;
        _setIn.Enabled = _setOut.Enabled = _reencode.Enabled = !busy;
        _playFromIn.Enabled = _previewEnd.Enabled = !busy;
        _status.Text = status;
    }

    public void ReportProgress(double frac)
    {
        int v = (int)Math.Clamp(frac * 1000, 0, 1000);
        _progress.Value = v;
        _status.Text = "Cutting… " + (v / 10) + "%";
    }

    public void ReportDone(bool success, string message)
    {
        SetBusy(false, message);
        _progress.Visible = false;
        if (success) Close();
        else _status.ForeColor = _theme.ErrorHighlight;
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.Panel;
        ForeColor = theme.Text;
        foreach (var lbl in new[] { _fileLabel, _inLabel, _outLabel, _selLabel, _status })
            lbl.ForeColor = theme.Text;
        _reencode.ForeColor = theme.Text;
        _stopAtOut.ForeColor = theme.Text;
        foreach (var b in new[] { _playPause, _playFromIn, _previewEnd, _setIn, _setOut, _cut, _close })
        {
            b.BackColor = theme.ButtonBack;
            b.ForeColor = theme.Text;
            b.FlatAppearance.BorderColor = theme.Border;
        }
        _range.ApplyTheme(theme);
        _fine.ApplyTheme(theme);
        SyncHint();
        if (IsHandleCreated) DarkChrome.ApplyTitleBar(Handle, theme.IsDark);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Don't let the user close mid-cut — the ffmpeg run and file swap are in
        // flight; closing would orphan the status reporting.
        if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _tick.Stop(); _tick.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
