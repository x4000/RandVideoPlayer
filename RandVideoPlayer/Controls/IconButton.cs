using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

// Flat button with a Segoe MDL2 Assets glyph rendered geometrically centered.
// Supports theming, hover/press/disabled states, and an optional square badge
// in the top-right corner (used to flag the error button).
public sealed class IconButton : Control, IThemedControl
{
    private string _glyph;
    private bool _hovered;
    private bool _pressed;
    private Theme _theme = Theme.Dark;
    private static readonly Font IconFont = new Font("Segoe MDL2 Assets", 11f, FontStyle.Regular, GraphicsUnit.Point);

    public Color? BadgeColor { get; set; }

    public string Glyph
    {
        get => _glyph;
        set { _glyph = value ?? ""; Invalidate(); }
    }

    public IconButton(string glyph, string tooltip)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        _glyph = glyph;
        Size = new Size(34, 30);
        Margin = new Padding(2);
        TabStop = false;
        Cursor = Cursors.Hand;
        var tt = new ToolTip();
        tt.SetToolTip(this, tooltip);
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        ForeColor = theme.Text;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    public void PerformClick() { OnClick(EventArgs.Empty); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Color back = _pressed ? _theme.ButtonActive
                   : _hovered ? _theme.ButtonHover
                   : _theme.ButtonBack;

        using (var bb = new SolidBrush(back)) g.FillRectangle(bb, ClientRectangle);
        using (var pen = new Pen(_theme.Border, 1f))
        {
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            g.DrawRectangle(pen, r);
        }

        if (!string.IsNullOrEmpty(_glyph))
        {
            // Geometrically center the glyph using MeasureString and adjust for baseline.
            var size = g.MeasureString(_glyph, IconFont, PointF.Empty, StringFormat.GenericTypographic);
            float x = (Width - size.Width) / 2f;
            float y = (Height - size.Height) / 2f;
            using var tb = new SolidBrush(_theme.Text);
            g.DrawString(_glyph, IconFont, tb, x, y, StringFormat.GenericTypographic);
        }

        if (BadgeColor.HasValue)
        {
            int bs = 8;
            var badge = new Rectangle(Width - bs - 3, 3, bs, bs);
            using var bb = new SolidBrush(BadgeColor.Value);
            g.FillEllipse(bb, badge);
            using var bp = new Pen(Color.FromArgb(200, 0, 0, 0), 1f);
            g.DrawEllipse(bp, badge);
        }
    }
}
