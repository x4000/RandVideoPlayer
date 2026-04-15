using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

// Horizontal volume slider with green->yellow->red gradient fill.
public sealed class VolumeSlider : Control, IThemedControl
{
    private Theme _theme = Theme.Dark;
    public void ApplyTheme(Theme theme) { _theme = theme; BackColor = theme.Panel; Invalidate(); }
    private int _value = 80;
    private bool _dragging;
    public int MaxValue { get; set; } = 150;

    public event Action<int>? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxValue);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(_value);
        }
    }

    public VolumeSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        Width = 120;
        Height = 18;
        BackColor = _theme.Panel;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int padX = 4;
        int railY = Height / 2 - 4;
        var rail = new Rectangle(padX, railY, Width - padX * 2, 8);

        using (var back = new SolidBrush(_theme.VolumeRail))
            g.FillRectangle(back, rail);

        float pct = MaxValue > 0 ? (float)_value / MaxValue : 0f;
        int fillW = (int)(rail.Width * pct);

        if (fillW > 0)
        {
            // Gradient: green (0) -> yellow (~66%) -> red (100%).
            var gradRect = new Rectangle(rail.X, rail.Y, rail.Width, rail.Height);
            using var blend = new LinearGradientBrush(gradRect,
                Color.FromArgb(110, 200, 80), Color.FromArgb(210, 60, 60),
                LinearGradientMode.Horizontal);
            var cb = new ColorBlend
            {
                Positions = new[] { 0f, 0.66f, 1f },
                Colors = new[]
                {
                    Color.FromArgb(110, 200, 80),
                    Color.FromArgb(235, 205, 60),
                    Color.FromArgb(210, 60, 60)
                }
            };
            blend.InterpolationColors = cb;
            var fillRect = new Rectangle(rail.X, rail.Y, fillW, rail.Height);
            g.SetClip(fillRect);
            g.FillRectangle(blend, gradRect);
            g.ResetClip();
        }

        // Tick at 100% position.
        if (MaxValue > 100)
        {
            int tickX = rail.X + (int)(rail.Width * (100f / MaxValue));
            using var tickPen = new Pen(_theme.TextMuted, 1f);
            g.DrawLine(tickPen, tickX, rail.Y - 2, tickX, rail.Bottom + 2);
        }

        // Thumb: small triangle pointer above rail.
        int thumbX = rail.X + fillW;
        var tri = new[]
        {
            new Point(thumbX - 4, rail.Y - 1),
            new Point(thumbX + 4, rail.Y - 1),
            new Point(thumbX, rail.Y + 5)
        };
        using (var triBrush = new SolidBrush(_theme.Text))
            g.FillPolygon(triBrush, tri);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _dragging = true; SetFromMouse(e.X); }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromMouse(e.X);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = Value + (e.Delta > 0 ? 5 : -5);
    }

    private void SetFromMouse(int x)
    {
        int padX = 4;
        int railW = Width - padX * 2;
        if (railW <= 0) return;
        float pct = Math.Clamp((x - padX) / (float)railW, 0f, 1f);
        Value = (int)Math.Round(pct * MaxValue);
    }
}
