using System.Drawing;

namespace RandVideoPlayer.UI;

public sealed class Theme
{
    public bool IsDark { get; init; }
    public Color Background { get; init; }
    public Color Panel { get; init; }
    public Color PanelAlt { get; init; }       // toolbar / sidebar
    public Color Text { get; init; }
    public Color TextMuted { get; init; }
    public Color Border { get; init; }
    public Color ButtonBack { get; init; }
    public Color ButtonHover { get; init; }
    public Color ButtonActive { get; init; }
    public Color ListRowEven { get; init; }
    public Color ListRowOdd { get; init; }
    public Color ListSelection { get; init; }
    public Color CurrentTrack { get; init; }
    public Color Accent { get; init; }          // scrubber fill, selected tab
    public Color ScrubberRail { get; init; }
    public Color VolumeRail { get; init; }
    public Color MenuBack { get; init; }
    public Color ErrorBack { get; init; }
    public Color ErrorBar { get; init; }
    public Color ErrorHighlight { get; init; }  // for error button when unread

    public static Theme Dark => new()
    {
        IsDark = true,
        Background = Color.FromArgb(20, 20, 22),
        Panel = Color.FromArgb(32, 32, 36),
        PanelAlt = Color.FromArgb(42, 42, 48),
        Text = Color.FromArgb(225, 225, 228),
        TextMuted = Color.FromArgb(165, 165, 170),
        Border = Color.FromArgb(65, 65, 72),
        ButtonBack = Color.FromArgb(55, 55, 62),
        ButtonHover = Color.FromArgb(75, 75, 85),
        ButtonActive = Color.FromArgb(60, 110, 180),
        ListRowEven = Color.FromArgb(36, 36, 40),
        ListRowOdd = Color.FromArgb(42, 42, 46),
        ListSelection = Color.FromArgb(60, 95, 150),
        CurrentTrack = Color.FromArgb(110, 80, 30),
        Accent = Color.FromArgb(80, 150, 230),
        ScrubberRail = Color.FromArgb(65, 65, 72),
        VolumeRail = Color.FromArgb(65, 65, 72),
        MenuBack = Color.FromArgb(42, 42, 48),
        ErrorBack = Color.FromArgb(50, 40, 30),
        ErrorBar = Color.FromArgb(80, 65, 45),
        ErrorHighlight = Color.FromArgb(200, 80, 70)
    };

    public static Theme Light => new()
    {
        IsDark = false,
        Background = Color.FromArgb(245, 245, 245),
        Panel = Color.FromArgb(240, 240, 240),
        PanelAlt = Color.FromArgb(235, 235, 235),
        Text = Color.FromArgb(40, 40, 40),
        TextMuted = Color.FromArgb(100, 100, 100),
        Border = Color.FromArgb(200, 200, 200),
        ButtonBack = Color.FromArgb(235, 235, 235),
        ButtonHover = Color.FromArgb(220, 220, 220),
        ButtonActive = Color.FromArgb(220, 232, 250),
        ListRowEven = Color.White,
        ListRowOdd = Color.FromArgb(250, 250, 250),
        ListSelection = Color.FromArgb(200, 220, 245),
        CurrentTrack = Color.FromArgb(255, 243, 200),
        Accent = Color.FromArgb(64, 132, 214),
        ScrubberRail = Color.FromArgb(210, 210, 210),
        VolumeRail = Color.FromArgb(225, 225, 225),
        MenuBack = Color.FromArgb(240, 240, 240),
        ErrorBack = Color.FromArgb(250, 245, 230),
        ErrorBar = Color.FromArgb(235, 220, 190),
        ErrorHighlight = Color.FromArgb(220, 90, 70)
    };
}

public interface IThemedControl
{
    void ApplyTheme(Theme theme);
}
