using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

/// <summary>
/// A trim bar for the cut window: a rail with a highlighted [In, Out] selection,
/// draggable In/Out handles, and a playhead. Dragging a handle moves that marker;
/// clicking/dragging elsewhere seeks the playhead. All values are milliseconds.
/// </summary>
public sealed class RangeScrubber : Control, IThemedControl
{
    private Theme _theme = Theme.Dark;
    private long _lengthMs;
    private long _inMs;
    private long _outMs;
    private long _playheadMs;

    private enum Grab { None, In, Out, Seek }
    private Grab _grab = Grab.None;

    private const int RailPad = 12;
    private const int HandleHalf = 5;

    public event Action<long>? InChanged;
    public event Action<long>? OutChanged;
    public event Action<long>? SeekRequested; // fired while scrubbing the playhead

    public RangeScrubber()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        Height = 54;
        Cursor = Cursors.Hand;
        BackColor = _theme.Panel;
    }

    public void ApplyTheme(Theme theme) { _theme = theme; BackColor = theme.Panel; Invalidate(); }

    public long LengthMs
    {
        get => _lengthMs;
        set { _lengthMs = Math.Max(0, value); _outMs = Math.Clamp(_outMs <= 0 ? _lengthMs : _outMs, 0, _lengthMs); Invalidate(); }
    }

    public long InMs
    {
        get => _inMs;
        set { _inMs = Math.Clamp(value, 0, _lengthMs); if (_inMs > _outMs) _outMs = _inMs; Invalidate(); }
    }

    public long OutMs
    {
        get => _outMs;
        set { _outMs = Math.Clamp(value, 0, _lengthMs); if (_outMs < _inMs) _inMs = _outMs; Invalidate(); }
    }

    public long PlayheadMs
    {
        get => _playheadMs;
        set { if (_grab == Grab.None) { _playheadMs = Math.Clamp(value, 0, _lengthMs); Invalidate(); } }
    }

    private int RailW => Math.Max(1, Width - RailPad * 2);
    private int XForMs(long ms) => _lengthMs <= 0 ? RailPad : RailPad + (int)((double)ms / _lengthMs * RailW);
    private long MsForX(int x)
    {
        if (_lengthMs <= 0) return 0;
        double frac = Math.Clamp((x - RailPad) / (double)RailW, 0, 1);
        return (long)(frac * _lengthMs);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int midY = Height / 2;
        var rail = new Rectangle(RailPad, midY - 3, RailW, 6);
        using (var b = new SolidBrush(_theme.ScrubberRail)) g.FillRectangle(b, rail);

        // Selection band.
        int xIn = XForMs(_inMs);
        int xOut = XForMs(_outMs);
        if (xOut > xIn)
        {
            var sel = new Rectangle(xIn, rail.Y, xOut - xIn, rail.Height);
            using var b = new SolidBrush(_theme.Accent);
            g.FillRectangle(b, sel);
        }

        // Playhead.
        int xPh = XForMs(_playheadMs);
        using (var pen = new Pen(_theme.Text, 1.5f))
            g.DrawLine(pen, xPh, midY - 14, xPh, midY + 14);

        // In / Out handles.
        DrawHandle(g, xIn, midY, true);
        DrawHandle(g, xOut, midY, false);
    }

    private void DrawHandle(Graphics g, int x, int midY, bool isIn)
    {
        var r = new Rectangle(x - HandleHalf, midY - 12, HandleHalf * 2, 24);
        using (var b = new SolidBrush(_theme.Accent)) g.FillRectangle(b, r);
        using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0), 1f)) g.DrawRectangle(pen, r);
        // little notch to hint drag direction
        using var line = new Pen(_theme.IsDark ? Color.White : Color.Black, 1f);
        g.DrawLine(line, x, midY - 6, x, midY + 6);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int xIn = XForMs(_inMs);
        int xOut = XForMs(_outMs);
        // Prefer whichever handle the click is closest to when both are near.
        bool nearIn = Math.Abs(e.X - xIn) <= HandleHalf + 3;
        bool nearOut = Math.Abs(e.X - xOut) <= HandleHalf + 3;
        if (nearIn && nearOut) { _grab = Math.Abs(e.X - xIn) <= Math.Abs(e.X - xOut) ? Grab.In : Grab.Out; }
        else if (nearIn) _grab = Grab.In;
        else if (nearOut) _grab = Grab.Out;
        else _grab = Grab.Seek;
        DragTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_grab != Grab.None) DragTo(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _grab = Grab.None;
    }

    private void DragTo(int x)
    {
        long ms = MsForX(x);
        switch (_grab)
        {
            case Grab.In:
                _inMs = Math.Clamp(ms, 0, _outMs);
                InChanged?.Invoke(_inMs);
                break;
            case Grab.Out:
                _outMs = Math.Clamp(ms, _inMs, _lengthMs);
                OutChanged?.Invoke(_outMs);
                break;
            case Grab.Seek:
                _playheadMs = ms;
                SeekRequested?.Invoke(_playheadMs);
                break;
        }
        Invalidate();
    }
}
