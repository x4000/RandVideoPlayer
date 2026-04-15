using System;
using System.Drawing;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

public sealed class TransportBar : UserControl, IThemedControl
{
    public ScrubberBar Scrubber { get; }
    public VolumeSlider Volume { get; }

    public IconButton PlayPauseBtn { get; }
    public IconButton PrevBtn { get; }
    public IconButton NextBtn { get; }
    public IconButton ReshuffleBtn { get; }
    public IconButton MuteBtn { get; }
    public IconButton SidebarBtn { get; }
    public IconButton ErrorPanelBtn { get; }
    public IconButton ThemeBtn { get; }

    public Label ElapsedLabel { get; }
    public Label TotalLabel { get; }
    public Label VolumeLabel { get; }
    public Label NowPlayingLabel { get; }

    // Segoe MDL2 Assets glyphs
    private const string GLYPH_PREV = "\uE100";
    private const string GLYPH_NEXT = "\uE101";
    private const string GLYPH_PLAY = "\uE768";
    private const string GLYPH_PAUSE = "\uE769";
    private const string GLYPH_SHUFFLE = "\uE8B1";
    private const string GLYPH_MUTE = "\uE74F";
    private const string GLYPH_SPEAKER = "\uE767";
    private const string GLYPH_LIST = "\uE8FD";
    private const string GLYPH_ERROR = "\uE783";
    private const string GLYPH_THEME = "\uE706";   // brightness / theme

    private Theme _theme = Theme.Dark;
    private bool _errorHighlighted;

    public TransportBar()
    {
        Height = 72;
        Dock = DockStyle.Bottom;

        ElapsedLabel = MakeTimeLabel("00:00");
        TotalLabel = MakeTimeLabel("00:00");
        Scrubber = new ScrubberBar { Dock = DockStyle.Fill };

        var row1 = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, Dock = DockStyle.Top, Height = 24
        };
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        row1.Controls.Add(ElapsedLabel, 0, 0);
        row1.Controls.Add(Scrubber, 1, 0);
        row1.Controls.Add(TotalLabel, 2, 0);

        PrevBtn = new IconButton(GLYPH_PREV, "Previous (Mouse 4)");
        PlayPauseBtn = new IconButton(GLYPH_PLAY, "Play / Pause (Space)");
        NextBtn = new IconButton(GLYPH_NEXT, "Next (Mouse 5)");
        ReshuffleBtn = new IconButton(GLYPH_SHUFFLE, "Reshuffle playlist");
        SidebarBtn = new IconButton(GLYPH_LIST, "Toggle sidebar (F9)");
        ErrorPanelBtn = new IconButton(GLYPH_ERROR, "Toggle error panel");
        MuteBtn = new IconButton(GLYPH_SPEAKER, "Mute / Unmute");
        ThemeBtn = new IconButton(GLYPH_THEME, "Toggle dark / light mode");

        Volume = new VolumeSlider { Width = 120, Height = 24, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        VolumeLabel = new Label
        {
            Text = "80%", AutoSize = false, Width = 42, Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8.5f), BackColor = Color.Transparent
        };

        NowPlayingLabel = new Label
        {
            Text = "", AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9f), BackColor = Color.Transparent,
            AutoEllipsis = true, Padding = new Padding(8, 0, 8, 0)
        };

        var leftButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true,
            Dock = DockStyle.Left, WrapContents = false,
            BackColor = Color.Transparent, Padding = new Padding(2, 4, 0, 4)
        };
        leftButtons.Controls.AddRange(new Control[] { PrevBtn, PlayPauseBtn, NextBtn, ReshuffleBtn });

        var rightControls = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true,
            Dock = DockStyle.Right, WrapContents = false,
            BackColor = Color.Transparent, Padding = new Padding(0, 4, 4, 4)
        };
        rightControls.Controls.AddRange(new Control[] { ErrorPanelBtn, SidebarBtn, ThemeBtn, MuteBtn, Volume, VolumeLabel });

        var row2 = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        row2.Controls.Add(NowPlayingLabel);
        row2.Controls.Add(rightControls);
        row2.Controls.Add(leftButtons);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(row1, 0, 0);
        outer.Controls.Add(row2, 0, 1);
        Controls.Add(outer);

        ApplyTheme(_theme);
    }

    private static Label MakeTimeLabel(string initial)
    {
        return new Label
        {
            Text = initial, AutoSize = false, Width = 56, Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f), BackColor = Color.Transparent
        };
    }

    public void SetPlayPauseGlyph(bool playing)
    {
        PlayPauseBtn.Glyph = playing ? GLYPH_PAUSE : GLYPH_PLAY;
    }

    public void SetMuteGlyph(bool muted)
    {
        MuteBtn.Glyph = muted ? GLYPH_MUTE : GLYPH_SPEAKER;
    }

    public void SetErrorHighlighted(bool on)
    {
        _errorHighlighted = on;
        ErrorPanelBtn.BadgeColor = on ? _theme.ErrorHighlight : (Color?)null;
        ErrorPanelBtn.Invalidate();
    }

    public static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.Panel;
        ElapsedLabel.ForeColor = theme.TextMuted;
        TotalLabel.ForeColor = theme.TextMuted;
        VolumeLabel.ForeColor = theme.TextMuted;
        NowPlayingLabel.ForeColor = theme.Text;
        foreach (Control c in new Control[] { PrevBtn, PlayPauseBtn, NextBtn, ReshuffleBtn, SidebarBtn, ErrorPanelBtn, MuteBtn, ThemeBtn })
            ((IconButton)c).ApplyTheme(theme);
        Scrubber.ApplyTheme(theme);
        Volume.ApplyTheme(theme);
        ErrorPanelBtn.BadgeColor = _errorHighlighted ? _theme.ErrorHighlight : (Color?)null;
        Invalidate(true);
    }
}
