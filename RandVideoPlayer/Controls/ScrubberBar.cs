using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

// Custom scrubber: flat rail, accent-colored fill, round accent thumb.
// Colors come from the active Theme.
public sealed class ScrubberBar : Control, IThemedControl
{
    private Theme _theme = Theme.Dark;
    public void ApplyTheme(Theme theme) { _theme = theme; BackColor = theme.Panel; Invalidate(); }
    private long _lengthMs = 0;
    private long _timeMs = 0;
    private bool _dragging;

    public event Action<long>? SeekRequested; // final seek (mouse up)
    public event Action<long>? SeekPreview;   // during drag (for label update)

    public long LengthMs
    {
        get => _lengthMs;
        set { _lengthMs = Math.Max(0, value); Invalidate(); }
    }

    public long TimeMs
    {
        get => _timeMs;
        set { if (!_dragging) { _timeMs = Math.Max(0, value); Invalidate(); } }
    }

    public ScrubberBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        Height = 18;
        BackColor = _theme.Panel;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int midY = Height / 2;
        int railPad = 8;
        var railRect = new Rectangle(railPad, midY - 3, Width - railPad * 2, 6);

        using (var back = new SolidBrush(_theme.ScrubberRail))
            g.FillRectangle(back, railRect);

        float pct = _lengthMs > 0 ? Math.Clamp((float)_timeMs / _lengthMs, 0f, 1f) : 0f;
        int fillW = (int)(railRect.Width * pct);
        if (fillW > 0)
        {
            var fillRect = new Rectangle(railRect.X, railRect.Y, fillW, railRect.Height);
            using var fill = new SolidBrush(_theme.Accent);
            g.FillRectangle(fill, fillRect);
        }

        // Thumb
        int thumbCx = railRect.X + fillW;
        int thumbR = 7;
        var thumbRect = new Rectangle(thumbCx - thumbR, midY - thumbR, thumbR * 2, thumbR * 2);
        using (var thumb = new SolidBrush(_theme.Accent))
            g.FillEllipse(thumb, thumbRect);
        using (var ring = new Pen(Color.FromArgb(180, 0, 0, 0), 1f))
            g.DrawEllipse(ring, thumbRect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            SetFromMouse(e.X, fireSeek: false);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromMouse(e.X, fireSeek: false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging && e.Button == MouseButtons.Left)
        {
            _dragging = false;
            SetFromMouse(e.X, fireSeek: true);
        }
    }

    private void SetFromMouse(int x, bool fireSeek)
    {
        int railPad = 8;
        int railW = Width - railPad * 2;
        if (railW <= 0 || _lengthMs <= 0) return;
        float pct = Math.Clamp((x - railPad) / (float)railW, 0f, 1f);
        _timeMs = (long)(pct * _lengthMs);
        Invalidate();
        if (fireSeek) SeekRequested?.Invoke(_timeMs);
        else SeekPreview?.Invoke(_timeMs);
    }
}
