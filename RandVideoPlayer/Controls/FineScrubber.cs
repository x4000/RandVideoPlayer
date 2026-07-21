using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

/// <summary>
/// A zoomed "magnifier" scrubber for precision seeking. It shows a small time
/// window (default ±5s, mouse-wheel to zoom 2–60s total) centered on the current
/// playhead, with per-second ticks. Because the whole width maps to only a few
/// seconds, dragging seeks at much finer resolution than the full-length bar.
/// Seek-only — In/Out are set with the buttons once you've scrubbed into place.
/// </summary>
public sealed class FineScrubber : Control, IThemedControl
{
    private Theme _theme = Theme.Dark;
    private long _lengthMs;
    private long _playheadMs;
    private long _inMs;
    private long _outMs;
    private long _spanMs = 10_000;   // total visible window width
    private bool _dragging;
    private long _frozenCenter;       // center held steady while dragging

    private const int Pad = 12;
    private const long MinSpan = 2_000;
    private const long MaxSpan = 60_000;

    public event Action<long>? SeekRequested;

    public FineScrubber()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.Selectable, true);
        TabStop = false;
        Height = 54;
        Cursor = Cursors.SizeWE;
        BackColor = _theme.Panel;
    }

    public void ApplyTheme(Theme theme) { _theme = theme; BackColor = theme.Panel; Invalidate(); }

    public long LengthMs { get => _lengthMs; set { _lengthMs = Math.Max(0, value); Invalidate(); } }
    public long InMs { get => _inMs; set { _inMs = value; Invalidate(); } }
    public long OutMs { get => _outMs; set { _outMs = value; Invalidate(); } }
    public long PlayheadMs
    {
        get => _playheadMs;
        set { if (!_dragging) { _playheadMs = Math.Clamp(value, 0, Math.Max(0, _lengthMs)); Invalidate(); } }
    }

    public long SpanMs => _spanMs;

    private long Center => _dragging ? _frozenCenter : _playheadMs;
    private long WindowStart => Center - _spanMs / 2;
    private int RailW => Math.Max(1, Width - Pad * 2);
    private int XForMs(long ms) => Pad + (int)((ms - WindowStart) / (double)_spanMs * RailW);
    private long MsForX(int x)
    {
        double frac = (x - Pad) / (double)RailW;
        return WindowStart + (long)(frac * _spanMs);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int midY = Height / 2 - 5;
        var rail = new Rectangle(Pad, midY - 3, RailW, 6);
        using (var b = new SolidBrush(_theme.ScrubberRail)) g.FillRectangle(b, rail);

        long winStart = WindowStart;
        long winEnd = winStart + _spanMs;

        // Selection region (In..Out) clipped to the visible window.
        if (_outMs > _inMs)
        {
            long a = Math.Max(_inMs, winStart);
            long bb = Math.Min(_outMs, winEnd);
            if (bb > a)
            {
                int xa = XForMs(a), xb = XForMs(bb);
                using var sb = new SolidBrush(Color.FromArgb(90, _theme.Accent));
                g.FillRectangle(sb, new Rectangle(xa, rail.Y, Math.Max(1, xb - xa), rail.Height));
            }
        }

        // Per-second ticks + labels.
        using (var tickPen = new Pen(_theme.TextMuted, 1f))
        using (var txtBrush = new SolidBrush(_theme.TextMuted))
        using (var font = new Font("Segoe UI", 7f))
        {
            long firstSec = (long)Math.Ceiling(winStart / 1000.0);
            long lastSec = (long)Math.Floor(winEnd / 1000.0);
            long visibleSecs = Math.Max(1, lastSec - firstSec);
            long labelEvery = visibleSecs <= 12 ? 1 : (visibleSecs <= 30 ? 5 : 10);
            for (long s = firstSec; s <= lastSec; s++)
            {
                long ms = s * 1000;
                if (ms < 0 || (_lengthMs > 0 && ms > _lengthMs)) continue;
                int x = XForMs(ms);
                bool labeled = (s % labelEvery) == 0;
                g.DrawLine(tickPen, x, midY + 6, x, midY + (labeled ? 14 : 10));
                if (labeled)
                {
                    string t = FormatSec(ms);
                    var sz = g.MeasureString(t, font);
                    g.DrawString(t, font, txtBrush, x - sz.Width / 2, midY + 15);
                }
            }
        }

        // In / Out markers if within the window.
        DrawMarker(g, _inMs, winStart, winEnd, midY, true);
        DrawMarker(g, _outMs, winStart, winEnd, midY, false);

        // Playhead.
        int xph = XForMs(_playheadMs);
        if (xph >= Pad - 1 && xph <= Width - Pad + 1)
        {
            using var pen = new Pen(_theme.Text, 1.5f);
            g.DrawLine(pen, xph, midY - 12, xph, midY + 6);
        }

        // Span readout.
        using (var font = new Font("Segoe UI", 7.5f))
        using (var br = new SolidBrush(_theme.TextMuted))
            g.DrawString("±" + (_spanMs / 2000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s  (wheel to zoom)",
                         font, br, Pad, 1);
    }

    private void DrawMarker(Graphics g, long ms, long winStart, long winEnd, int midY, bool isIn)
    {
        if (ms < winStart || ms > winEnd) return;
        int x = XForMs(ms);
        using var pen = new Pen(_theme.Accent, 2f);
        g.DrawLine(pen, x, midY - 12, x, midY + 6);
        using var br = new SolidBrush(_theme.Accent);
        var tri = isIn
            ? new[] { new Point(x, midY - 12), new Point(x + 6, midY - 12), new Point(x, midY - 6) }
            : new[] { new Point(x, midY - 12), new Point(x - 6, midY - 12), new Point(x, midY - 6) };
        g.FillPolygon(br, tri);
    }

    private static string FormatSec(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", ts.Minutes, ts.Seconds);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        // Needed so the wheel targets this lane while hovered.
        if (CanFocus) Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _frozenCenter = _playheadMs;   // hold the window steady during the drag
        SeekTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SeekTo(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        _spanMs = (long)Math.Clamp(_spanMs * factor, MinSpan, MaxSpan);
        Invalidate();
    }

    private void SeekTo(int x)
    {
        long ms = Math.Clamp(MsForX(x), 0, _lengthMs > 0 ? _lengthMs : long.MaxValue);
        _playheadMs = ms;
        SeekRequested?.Invoke(ms);
        Invalidate();
    }
}
