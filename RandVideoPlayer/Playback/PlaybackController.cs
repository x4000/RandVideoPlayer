using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace RandVideoPlayer.Playback;

public sealed class PlaybackController : IDisposable
{
    private readonly LibVLC _libVlc;
    public LibVLC LibVlcInstance => _libVlc;
    public MediaPlayer Player { get; }

    public event Action? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action? StateChanged;

    private System.Windows.Forms.Timer? _watchdog;
    private string? _currentPath;
    private bool _sawPlaying;
    private int _desiredVolume = 80;
    private bool _desiredMuted;
    private Media? _currentMedia;
    // Captured at construction on the UI thread. All callbacks libvlc fires on
    // its own event thread are marshaled here before touching MediaPlayer state.
    // Without this, rapid track changes can deadlock: UI thread inside
    // Player.Play(...) holds a libvlc lock while vlc's event thread runs our
    // Playing handler and tries to take the same lock via Player.Mute/Volume.
    private readonly SynchronizationContext? _ui;

    public PlaybackController()
    {
        _ui = SynchronizationContext.Current;
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        Player = new MediaPlayer(_libVlc);

        Player.EndReached += (_, __) => RunOnUi(() => MediaEnded?.Invoke());
        Player.EncounteredError += (_, __) =>
            RunOnUi(() => MediaFailed?.Invoke("VLC EncounteredError on " + (_currentPath ?? "?")));
        Player.Playing += (_, __) =>
        {
            _sawPlaying = true;
            RunOnUi(() => { ReapplyAudio(); StateChanged?.Invoke(); });
        };
        Player.Paused += (_, __) => RunOnUi(() => StateChanged?.Invoke());
        Player.Stopped += (_, __) => RunOnUi(() => StateChanged?.Invoke());
        Player.TimeChanged += (_, __) => RunOnUi(() => StateChanged?.Invoke());
    }

    private void RunOnUi(Action action)
    {
        if (_ui != null) _ui.Post(_ => { try { action(); } catch { } }, null);
        else { try { action(); } catch { } }
    }

    public void AttachTo(LibVLCSharp.WinForms.VideoView view)
    {
        view.MediaPlayer = Player;
    }

    public void Play(string fullPath)
    {
        _currentPath = fullPath;
        _sawPlaying = false;
        if (!File.Exists(fullPath))
        {
            MediaFailed?.Invoke("File not found: " + fullPath);
            return;
        }
        try
        {
            var media = new Media(_libVlc, new Uri(fullPath));
            try { _currentMedia?.Dispose(); } catch { }
            _currentMedia = media;
            Player.Play(media);
            // Apply audio state once now (for backends that accept it pre-pipeline)
            // and again on the Playing event (for backends that need the pipeline live).
            ReapplyAudio();
            StartWatchdog();
        }
        catch (Exception ex)
        {
            MediaFailed?.Invoke($"Exception starting playback: {ex.Message}");
        }
    }

    // Some backends ignore volume writes while muted, so always unmute first,
    // set volume, then re-assert the desired mute state.
    private void ReapplyAudio()
    {
        try { Player.Mute = false; } catch { }
        try { Player.Volume = _desiredVolume; } catch { }
        if (_desiredMuted) { try { Player.Mute = true; } catch { } }
    }

    private void StartWatchdog()
    {
        _watchdog?.Stop();
        _watchdog?.Dispose();
        _watchdog = new System.Windows.Forms.Timer { Interval = 3500 };
        _watchdog.Tick += (_, __) =>
        {
            _watchdog?.Stop();
            if (!_sawPlaying)
                MediaFailed?.Invoke("Playback watchdog: no Playing event within 3.5s for " + (_currentPath ?? "?"));
        };
        _watchdog.Start();
    }

    public void TogglePause()
    {
        if (Player.State == VLCState.Playing) Player.Pause();
        else if (Player.State == VLCState.Paused) Player.SetPause(false);
        else if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath)) Play(_currentPath);
    }

    public void Stop() => Player.Stop();

    public long LengthMs => Player.Length;
    public long TimeMs
    {
        get => Player.Time;
        set { if (Player.IsSeekable) Player.Time = value; }
    }

    public int Volume
    {
        get => _desiredVolume;
        set
        {
            _desiredVolume = Math.Clamp(value, 0, 150);
            try { Player.Volume = _desiredVolume; } catch { }
        }
    }

    public bool Muted
    {
        get => _desiredMuted;
        set
        {
            _desiredMuted = value;
            try { Player.Mute = value; } catch { }
        }
    }

    public bool IsPlaying => Player.State == VLCState.Playing;

    public void Dispose()
    {
        try { _watchdog?.Dispose(); } catch { }
        try { Player.Stop(); } catch { }
        try { _currentMedia?.Dispose(); } catch { }
        try { Player.Dispose(); } catch { }
        try { _libVlc.Dispose(); } catch { }
    }
}
