using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.WinForms;

namespace RandVideoPlayer.Controls;

public sealed class VideoHost : UserControl
{
    public VideoView VideoView { get; }

    public VideoHost()
    {
        BackColor = Color.Black;
        Dock = DockStyle.Fill;
        VideoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
        Controls.Add(VideoView);
    }
}
