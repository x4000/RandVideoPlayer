using RandVideoPlayer.AppState;

namespace RandVideoPlayer;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        Application.Run(new MainForm(settings));
    }
}
