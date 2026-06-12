using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace RandVideoPlayer.Playback;

public sealed class PlaybackController : IDisposable
{
    private LibVLC _libVlc;
    public LibVLC LibVlcInstance => _libVlc;
    public MediaPlayer Player { get; private set; }

    public event Action? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action? StateChanged;
    /// <summary>
    /// Fired on the UI thread after <see cref="Recycle"/> has rebuilt both
    /// LibVLC and MediaPlayer. The host must reattach its VideoView to the
    /// new <see cref="Player"/> instance — the old one has been disposed.
    /// </summary>
    public event Action? PipelineRecycled;

    private System.Windows.Forms.Timer? _watchdog;
    private string? _currentPath;
    private bool _sawPlaying;
    private int _desiredVolume = 80;
    private bool _desiredMuted;
    private Media? _currentMedia;

    // Cached playback status, updated only from libvlc events (which fire on
    // libvlc's own thread). The UI thread reads THESE instead of calling
    // Player.Time / Player.Length / Player.State directly. Those property reads
    // take libvlc's player lock, which can be held for seconds — or deadlock
    // against a cross-thread video-window rebuild — while the Direct3D output
    // is wedged after a monitor power-off. The 250ms UI status timer hitting
    // Player.Time was exactly what froze the whole app when "Next" was pressed
    // on a black-screened pipeline. Reading cached values keeps the UI thread
    // completely decoupled from libvlc's internal locks.
    private long _cachedTimeMs;
    private long _cachedLengthMs;
    private volatile VLCState _cachedState = VLCState.NothingSpecial;

    // Captured at construction on the UI thread. All callbacks libvlc fires
    // on its own event thread are marshaled here before touching MediaPlayer
    // state. Without this, rapid track changes can deadlock: UI thread inside
    // Player.Play(...) holds a libvlc lock while vlc's event thread runs our
    // Playing handler and tries to take the same lock via Player.Mute/Volume.
    private readonly SynchronizationContext? _ui;

    // Dedicated worker thread that serializes all potentially-blocking libvlc
    // calls (Play, Stop, Dispose). This keeps the UI thread responsive even
    // when libvlc is wedged (e.g. after a monitor-sleep GPU reset leaves the
    // D3D video output in a broken state). The UI thread only ever ENQUEUES
    // work onto _workQueue and returns immediately.
    private readonly BlockingCollection<Action> _workQueue = new(new ConcurrentQueue<Action>());
    private readonly Thread _worker;
    private volatile bool _disposed;

    // Stamped (Environment.TickCount64) when the worker begins an item, reset
    // to 0 when it finishes. Lets the UI-side watchdog detect a libvlc call
    // that will never return — e.g. Stop/Play against a video output whose
    // D3D device died while the monitors were powered off. A wedged worker
    // starves the whole queue: every Play/Pause/Next silently no-ops while
    // the window itself stays responsive.
    private long _workStartedAtTicks;

    /// <summary>
    /// True if the worker has been stuck inside a single libvlc call for at
    /// least <paramref name="thresholdMs"/>. When this trips, the controller
    /// is beyond saving — <see cref="Abandon"/> it and build a fresh one.
    /// </summary>
    public bool WorkerLooksWedged(int thresholdMs)
    {
        long started = Interlocked.Read(ref _workStartedAtTicks);
        return started != 0 && Environment.TickCount64 - started >= thresholdMs;
    }

    public PlaybackController()
    {
        _ui = SynchronizationContext.Current;
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        Player = new MediaPlayer(_libVlc);
        WirePlayerEvents(Player);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RVP-VLC-Worker"
        };
        _worker.Start();
    }

    private void WirePlayerEvents(MediaPlayer p)
    {
        // Every handler first checks the event came from the CURRENT player.
        // Recycled/abandoned pipelines are torn down asynchronously on a
        // sacrificial thread and can keep firing events (a final Stopped, a
        // stale TimeChanged, even EndReached) after the new player is live —
        // without the guard those would corrupt the cached state, skip a
        // track, or log phantom errors.
        p.EndReached += (_, __) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            _cachedState = VLCState.Ended;
            RunOnUi(() => MediaEnded?.Invoke());
        };
        p.EncounteredError += (_, __) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            _cachedState = VLCState.Error;
            RunOnUi(() => MediaFailed?.Invoke("VLC EncounteredError on " + (_currentPath ?? "?")));
        };
        p.Playing += (_, __) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            _cachedState = VLCState.Playing;
            _sawPlaying = true;
            // Reapply audio on the WORKER, not the UI thread — Player.Mute/
            // Volume take the same player lock as everything else.
            EnqueueWork(ReapplyAudio);
            RunOnUi(() => StateChanged?.Invoke());
        };
        p.Paused += (_, __) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            _cachedState = VLCState.Paused;
            RunOnUi(() => StateChanged?.Invoke());
        };
        p.Stopped += (_, __) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            _cachedState = VLCState.Stopped;
            RunOnUi(() => StateChanged?.Invoke());
        };
        p.TimeChanged += (_, e) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            Interlocked.Exchange(ref _cachedTimeMs, e.Time);
            RunOnUi(() => StateChanged?.Invoke());
        };
        p.LengthChanged += (_, e) =>
        {
            if (!ReferenceEquals(p, Player)) return;
            Interlocked.Exchange(ref _cachedLengthMs, e.Length);
        };
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var work in _workQueue.GetConsumingEnumerable())
            {
                Interlocked.Exchange(ref _workStartedAtTicks, Environment.TickCount64);
                try { work(); }
                catch (Exception ex)
                {
                    RunOnUi(() => MediaFailed?.Invoke("Worker exception: " + ex.Message));
                }
                finally { Interlocked.Exchange(ref _workStartedAtTicks, 0); }
            }
        }
        catch { /* shutting down */ }
    }

    private void RunOnUi(Action action)
    {
        if (_ui != null) _ui.Post(_ => { try { action(); } catch { } }, null);
        else { try { action(); } catch { } }
    }

    private void EnqueueWork(Action work)
    {
        if (_disposed) return;
        try { _workQueue.Add(work); } catch (InvalidOperationException) { /* CompleteAdding'd */ }
    }

    public void AttachTo(LibVLCSharp.WinForms.VideoView view)
    {
        view.MediaPlayer = Player;
    }

    public void Play(string fullPath) => PlayAt(fullPath, 0);

    public void PlayAt(string fullPath, long startMs)
    {
        // Capture desired starting point synchronously, do the slow work async.
        _currentPath = fullPath;
        _sawPlaying = false;
        // Reset cached status now so the scrubber/labels don't briefly show the
        // previous track's position while the new media's events catch up.
        Interlocked.Exchange(ref _cachedTimeMs, Math.Max(0, startMs));
        Interlocked.Exchange(ref _cachedLengthMs, 0);
        if (!File.Exists(fullPath))
        {
            MediaFailed?.Invoke("File not found: " + fullPath);
            return;
        }
        EnqueueWork(() => PlayOnWorker(fullPath, startMs));
    }

    private void PlayOnWorker(string fullPath, long startMs)
    {
        // IMPORTANT: do NOT dispose the old media before Player.Play returns.
        // libvlc is still holding a ref to the currently-playing media until
        // Play(new) atomically swaps to the new one. Disposing first was a
        // known deadlock recipe — it released our last ref while vlc's event
        // thread was still firing events on the old media.
        Media? oldMedia = _currentMedia;
        try
        {
            var media = new Media(_libVlc, new Uri(fullPath));
            _currentMedia = media;
            Player.Play(media);
            // Apply audio state once now (for backends that accept it pre-pipeline)
            // and again on the Playing event (for backends that need the pipeline live).
            ReapplyAudio();
            if (startMs > 0)
            {
                try { Player.Time = startMs; } catch { }
            }
            // Arm the 3.5s no-Playing-event watchdog on the UI thread AFTER
            // Play has actually been issued — not before — so a slow worker
            // queue doesn't trigger false watchdog failures.
            RunOnUi(StartWatchdog);
        }
        catch (Exception ex)
        {
            RunOnUi(() => MediaFailed?.Invoke($"Exception starting playback: {ex.Message}"));
        }
        finally
        {
            // Now it's safe: the player has moved onto the new media (or the
            // attempt failed and we're no longer relying on old).
            try { oldMedia?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Full rebuild of the libvlc pipeline: creates a fresh LibVLC and
    /// MediaPlayer, fires <see cref="PipelineRecycled"/> so the host rebinds
    /// its VideoView, then replays the current file at the captured playhead.
    /// Used for every resume/display-wake recovery path and the manual Reset
    /// Player menu item. The OLD pipeline is torn down on a sacrificial
    /// thread, NOT the worker: Stop/Dispose on a player whose D3D video
    /// output died with the display can block forever, and doing that on the
    /// worker starved the queue — every subsequent Play/Pause/Next became a
    /// silent no-op (the morning-after symptom). If teardown wedges we leak
    /// that one pipeline; it dies with the process.
    /// </summary>
    public void Recycle()
    {
        var path = _currentPath;
        long resumeMs = Interlocked.Read(ref _cachedTimeMs);

        EnqueueWork(() =>
        {
            var oldPlayer = Player;
            var oldLibVlc = _libVlc;
            var oldMedia = _currentMedia;
            _currentMedia = null;
            _sawPlaying = false;
            _cachedState = VLCState.NothingSpecial;
            Interlocked.Exchange(ref _cachedTimeMs, resumeMs);
            Interlocked.Exchange(ref _cachedLengthMs, 0);

            LibVLC newVlc;
            MediaPlayer newPlayer;
            try
            {
                newVlc = new LibVLC("--no-video-title-show");
                newPlayer = new MediaPlayer(newVlc);
            }
            catch (Exception ex)
            {
                DisposeOnSacrificialThread(oldPlayer, oldLibVlc, oldMedia);
                RunOnUi(() => MediaFailed?.Invoke("Pipeline recycle failed: " + ex.Message));
                return;
            }
            WirePlayerEvents(newPlayer);
            _libVlc = newVlc;
            Player = newPlayer;

            // Audio (Volume/Mute) is reapplied by ReapplyAudio inside the
            // PlayAt -> PlayOnWorker path below, so we don't touch Player.* on
            // the UI thread here.
            RunOnUi(() =>
            {
                // Order matters: rebind the view FIRST — detaching calls into
                // the old player (Hwnd = 0), which must still be alive — then
                // hand the old pipeline to the sacrificial teardown thread.
                try { PipelineRecycled?.Invoke(); } catch { }
                DisposeOnSacrificialThread(oldPlayer, oldLibVlc, oldMedia);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    PlayAt(path, resumeMs);
            });
        });
    }

    // Tears a pipeline down on a throwaway background thread. Healthy case:
    // finishes in milliseconds. Wedged case (vout dead after display loss):
    // the thread blocks forever, we leak one pipeline, and nothing else is
    // affected — crucially, the worker queue keeps draining.
    private static void DisposeOnSacrificialThread(MediaPlayer? player, LibVLC? vlc, Media? media)
    {
        var t = new Thread(() =>
        {
            try { player?.Stop(); } catch { }
            try { media?.Dispose(); } catch { }
            try { player?.Dispose(); } catch { }
            try { vlc?.Dispose(); } catch { }
        })
        { IsBackground = true, Name = "RVP-VLC-Teardown" };
        t.Start();
    }

    /// <summary>
    /// Last-resort detach for when the worker thread itself is wedged inside
    /// a libvlc call that will never return (<see cref="WorkerLooksWedged"/>):
    /// stops accepting work and best-effort tears the pipeline down on a
    /// sacrificial thread. Never joins anything — the wedged worker is a
    /// background thread and simply dies with the process. The owner must
    /// discard this instance and build a fresh controller; queued-but-unrun
    /// commands are lost.
    /// </summary>
    public void Abandon()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watchdog?.Stop(); _watchdog?.Dispose(); } catch { }
        try { _workQueue.CompleteAdding(); } catch { }
        DisposeOnSacrificialThread(Player, _libVlc, _currentMedia);
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
        // Decide from cached state (no UI-thread libvlc read) and run the
        // actual pause/unpause on the worker, so a wedged pipeline can never
        // block the UI thread here.
        var st = _cachedState;
        if (st == VLCState.Playing) EnqueueWork(() => { try { Player.Pause(); } catch { } });
        else if (st == VLCState.Paused) EnqueueWork(() => { try { Player.SetPause(false); } catch { } });
        else if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath)) Play(_currentPath);
    }

    public void Stop()
    {
        // Route through the worker — Player.Stop() can block if the video
        // output is wedged.
        EnqueueWork(() => { try { Player.Stop(); } catch { } });
    }

    /// <summary>
    /// Runs <paramref name="uiCallback"/> on the UI thread after any
    /// currently-queued worker operations (Play, Stop, ReinitializeAfterResume)
    /// have finished. Use this to coordinate UI actions that need libvlc to
    /// have released its file handle — e.g. deleting the file that was just
    /// stopped, where SHFileOperation would otherwise hang on the open handle.
    /// </summary>
    public void RunAfterPendingWork(Action uiCallback)
    {
        EnqueueWork(() => RunOnUi(uiCallback));
    }

    // All of these read cached state (updated from libvlc events) and route
    // writes through the worker — the UI thread never calls into libvlc here.
    public long LengthMs => Interlocked.Read(ref _cachedLengthMs);
    public long TimeMs
    {
        get => Interlocked.Read(ref _cachedTimeMs);
        set
        {
            long target = Math.Max(0, value);
            // Reflect the seek immediately in the UI, then apply it on the worker.
            Interlocked.Exchange(ref _cachedTimeMs, target);
            EnqueueWork(() => { try { if (Player.IsSeekable) Player.Time = target; } catch { } });
        }
    }

    public int Volume
    {
        get => _desiredVolume;
        set
        {
            _desiredVolume = Math.Clamp(value, 0, 150);
            int v = _desiredVolume;
            EnqueueWork(() => { try { Player.Volume = v; } catch { } });
        }
    }

    public bool Muted
    {
        get => _desiredMuted;
        set
        {
            _desiredMuted = value;
            EnqueueWork(() => { try { Player.Mute = value; } catch { } });
        }
    }

    public bool IsPlaying => _cachedState == VLCState.Playing;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watchdog?.Dispose(); } catch { }

        // Hand final teardown to the worker so it serializes with any pending
        // Play/Stop, then signal end-of-queue and let the worker exit on its
        // own. Don't Join — the worker is a background thread and we don't
        // want form-close to hang if libvlc is wedged.
        try
        {
            _workQueue.Add(() =>
            {
                try { Player.Stop(); } catch { }
                try { _currentMedia?.Dispose(); } catch { }
                try { Player.Dispose(); } catch { }
                try { _libVlc.Dispose(); } catch { }
            });
            _workQueue.CompleteAdding();
        }
        catch { }
    }
}
