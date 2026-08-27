using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using RandVideoPlayer.AppState;
using RandVideoPlayer.Controls;
using RandVideoPlayer.Integrations;
using RandVideoPlayer.Library;
using RandVideoPlayer.Playback;
using RandVideoPlayer.UI;

namespace RandVideoPlayer;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    // Not readonly: when the engine wedges beyond recovery (a libvlc call on
    // the worker that never returns) it is abandoned wholesale and replaced —
    // see RebuildPlaybackEngine.
    private PlaybackController _playback;

    private readonly MenuStrip _menu;
    private readonly ToolStripMenuItem _recentMenu;
    private ToolStripMenuItem _darkItem = null!;
    private readonly VideoHost _videoHost;
    private readonly Sidebar _sidebar;
    private readonly ErrorPanel _errorPanel;
    private readonly TransportBar _transport;

    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly System.Windows.Forms.Timer _positionSaveTimer;
    private readonly System.Windows.Forms.Timer _memoryWatchdog;

    private FolderLibrary? _library;
    private ShuffleFile? _shuffle;
    private DurationIndex? _durations;

    // What the "next track" means right now. Shuffle is home; the other two are
    // excursions the user launches from a sidebar tab, and both hand control
    // back to the shuffle list (at the exact spot it was interrupted) when they
    // run out. _currentIndex therefore always refers to the SHUFFLE list and is
    // never disturbed by playing something from Search or Favorites.
    private enum PlayContext { Shuffle, OneShot, Favorites }
    private PlayContext _context = PlayContext.Shuffle;
    private int _currentIndex = -1;
    private List<string> _favorites = new();   // relative paths, user-ordered
    private int _favIndex = -1;
    private long _shuffleReturnMs;             // playhead of the interrupted shuffle track
    private string? _currentFullPath;
    private long _resumePositionMs = 0;
    private bool _resumeApplied = true;
    private FileSystemWatcher? _watcher;
    private System.Windows.Forms.Timer? _watcherDebounce;
    private readonly HashSet<string> _pendingAdds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRemoves = new(StringComparer.OrdinalIgnoreCase);

    private Theme _theme = Theme.Dark;
    private readonly ThreadMouseHook _mouseHook = new();
    private CutWindow? _cutWindow;
    private AudioWindow? _audioWindow;
    private AudioFx.Settings _audioFx = new();

    // Audio-preview excursion: a short processed excerpt is rendered to %TEMP%
    // and played on the MAIN player (no second libvlc pipeline — see the cut
    // tool for the same reasoning). While it runs, the file on screen is NOT a
    // library file, so auto-advance and position saving have to stand down.
    private bool _audioPreviewActive;
    private string? _audioPreviewFile;
    private long _audioPreviewReturnMs;
    private int _audioPreviewSeq;

    // Display power-state recovery. When the monitors power off (idle timeout
    // or the panel dropping its DP/HDMI link) while the PC stays awake, the GPU
    // can lose the Direct3D device libvlc renders into — black video, audio
    // fine. No system suspend happens, so PowerModes.Resume never fires. We
    // instead listen for the display turning off and back on and rebuild the
    // pipeline automatically the moment it returns.
    private IntPtr _displayNotify = IntPtr.Zero;
    private bool _displayWasOff;
    private int _lastScreenCount = -1;
    private System.Windows.Forms.Timer? _displayRecoveryTimer;
    private System.Windows.Forms.Timer? _detachedWindowAdoptionTimer;
    private int _detachedWindowAdoptionTicksRemaining;
    private readonly HashSet<IntPtr> _adoptedVideoWindows = new();

    // Engine-wedge watchdog. A libvlc call that never returns (Stop/Play/
    // Dispose against a video output whose D3D device died) permanently
    // starves the worker queue: the window stays responsive but every
    // Play/Pause/Next silently does nothing. Detection + full engine swap is
    // the only way out — the in-process equivalent of restarting the app.
    private System.Windows.Forms.Timer? _engineWatchdog;
    private long _lastEngineRebuildTicks;
    private int _consecutiveEngineRebuilds; // reset whenever playback actually reaches Playing
    private const int EngineWedgeThresholdMs = 10_000;   // generous: HDD spin-up on a Play can take seconds
    private const int EngineRebuildMinIntervalMs = 60_000; // never thrash rebuilds in a loop
    private const int MaxConsecutiveEngineRebuilds = 5;  // each abandoned engine leaks; bound the damage
    private int _automaticEngineRebuildsThisSession;
    private bool _automaticEngineRebuildLimitLogged;
    private const int MaxAutomaticEngineRebuildsPerSession = 4;

    // Escalation bridge. A light recovery (ReplayCurrent on the existing player)
    // can come up black with no Playing event; the 3.5s watchdog then fires
    // MediaFailed -> GoNext, which would just advance the playlist file-by-file
    // on a permanently black pipeline. After enough consecutive failures with no
    // successful Playing in between, escalate to a real pipeline rebuild instead
    // of skipping. Rate-limited so a genuinely undecodable file can't loop on it.
    private int _consecutivePlaybackFailures;
    private long _lastPlaybackEscalationTicks;
    private int _automaticPipelineRecyclesThisSession;
    private bool _automaticPipelineRecycleLimitLogged;
    private const int PlaybackFailuresBeforeEscalation = 4;
    private const int PlaybackEscalationMinIntervalMs = 30_000;
    private const int MaxAutomaticPipelineRecyclesPerSession = 4;

    private long _lastMemoryRecoveryTicks;
    private long _lastMemoryLogTicks;
    private bool _memoryRestartInProgress;
    private const long MemoryRecoveryPrivateBytes = 1L * 1024L * 1024L * 1024L;
    private const long MemoryEmergencyPrivateBytes = 4L * 1024L * 1024L * 1024L;
    private const int MemoryRecoveryMinIntervalMs = 10 * 60 * 1000;
    private const int MemoryLogMinIntervalMs = 5 * 60 * 1000;
    private static readonly string DiagnosticLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArcenSettings", "RandVideoPlayer", "diagnostics.log");

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _theme = _settings.DarkMode ? Theme.Dark : Theme.Light;
        _audioFx = FromPrefs(_settings.AudioFx);
        Text = "RandVideoPlayer";
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.Manual;
        ApplyInitialBounds();
        KeyPreview = true;

        TryLoadAppIcon();

        _playback = new PlaybackController();
        WirePlayback(_playback);

        _menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var openItem = new ToolStripMenuItem("&Open Folder...", null, (_, __) => PromptOpenFolder()) { ShortcutKeys = Keys.Control | Keys.O };
        _recentMenu = new ToolStripMenuItem("&Recent Folders");
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, __) => Close());
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { openItem, _recentMenu, new ToolStripSeparator(), exitItem });
        var viewMenu = new ToolStripMenuItem("&View");
        var sidebarItem = new ToolStripMenuItem("&Sidebar", null, (_, __) => ToggleSidebar()) { ShortcutKeys = Keys.F9 };
        var errorsItem = new ToolStripMenuItem("&Error Panel", null, (_, __) => ToggleErrorPanel());
        _darkItem = new ToolStripMenuItem("&Dark Mode", null, (_, __) => ToggleDarkMode()) { Checked = _settings.DarkMode };
        viewMenu.DropDownItems.AddRange(new ToolStripItem[] { sidebarItem, errorsItem, new ToolStripSeparator(), _darkItem });
        var plMenu = new ToolStripMenuItem("&Playlist");
        var reshuffleItem = new ToolStripMenuItem("&Reshuffle Now", null, (_, __) => ReshuffleWithConfirm());
        plMenu.DropDownItems.Add(reshuffleItem);

        var playerMenu = new ToolStripMenuItem("Play&er");
        var resetPlayerItem = new ToolStripMenuItem("&Reset Player",
            null, (_, __) => ResetPlayer())
        { ToolTipText = "Rebuild the video/audio pipeline. Use if video or audio stops working after the machine has been idle." };
        var audioItem = new ToolStripMenuItem("&Audio: Normalize / Compress…",
            null, (_, __) => { if (_currentFullPath != null) OpenAudioWindow(_currentFullPath); })
        { ToolTipText = "Peak / loudness normalization and multiband compression for the current file, applied in place with the video stream untouched." };
        playerMenu.DropDownItems.Add(resetPlayerItem);
        playerMenu.DropDownItems.Add(new ToolStripSeparator());
        playerMenu.DropDownItems.Add(audioItem);
        // Grey rather than silently no-op when there is nothing loaded to work on.
        playerMenu.DropDownOpening += (_, __) =>
            audioItem.Enabled = _currentFullPath != null && Ffmpeg.IsAvailable;

        _menu.Items.AddRange(new ToolStripItem[] { fileMenu, viewMenu, plMenu, playerMenu });

        _videoHost = new VideoHost();
        _playback.AttachTo(_videoHost.VideoView);
        // If WinForms ever recreates the VideoView's window handle (e.g. on a
        // display/topology change), LibVLCSharp will not re-point libvlc at the
        // new HWND on its own — video would escape into a standalone window.
        // Re-bind and replay onto the live handle when that happens.
        _videoHost.VideoView.HandleCreated += OnVideoViewHandleCreated;
        _videoHost.VideoView.Resize += (_, __) => FitAdoptedVideoWindows();

        _sidebar = new Sidebar { Visible = _settings.SidebarVisible };
        _sidebar.Mode = ParseSidebarTab(_settings.SidebarTab);
        _sidebar.IsFavorite = IsFavoritePath;
        _sidebar.PlayRequested += PlayFromSidebar;
        _sidebar.RevealRequested += ShellOps.RevealInExplorer;
        _sidebar.DeleteRequested += HandleDeleteFromSidebar;
        _sidebar.CutRequested += OpenCutWindow;
        _sidebar.AudioRequested += OpenAudioWindow;
        _sidebar.ViewModeChanged += _ => { RefreshSidebar(); _sidebar.EnsureCurrentVisible(); };
        _sidebar.SearchTextChanged += () => { if (_sidebar.Mode == Sidebar.ViewMode.Search) RefreshSidebar(); };
        _sidebar.AddFavoriteRequested += AddFavorite;
        _sidebar.FavoritesBtn.FileDropped += AddFavorite;
        _sidebar.RemoveFavoriteRequested += RemoveFavorite;
        _sidebar.FavoriteMoveRequested += MoveFavorite;

        _errorPanel = new ErrorPanel { Visible = _settings.ErrorPanelVisible };

        _transport = new TransportBar();
        _errorPanel.EntryLogged += () => _transport.SetErrorHighlighted(true);
        _errorPanel.Cleared += () => _transport.SetErrorHighlighted(false);
        _transport.Volume.Value = _settings.Volume;
        _playback.Volume = _settings.Volume;
        // Muted state is intentionally not persisted across launches — a stuck
        // mute silently defeating audio is far more confusing than having to
        // re-toggle mute once per session, so every launch starts unmuted.
        _settings.Muted = false;
        _playback.Muted = false;
        _transport.SetMuteGlyph(false);

        _transport.PlayPauseBtn.Click += (_, __) => { _playback.TogglePause(); };
        _transport.PrevBtn.Click += (_, __) =>
        {
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control) JumpByApproxHour(forward: false);
            else GoPrev();
        };
        _transport.NextBtn.Click += (_, __) =>
        {
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control) JumpByApproxHour(forward: true);
            else GoNext();
        };
        _transport.ReshuffleBtn.Click += (_, __) => ReshuffleWithConfirm();
        _transport.SidebarBtn.Click += (_, __) => ToggleSidebar();
        _transport.ErrorPanelBtn.Click += (_, __) =>
        {
            ToggleErrorPanel();
            // Viewing the panel clears the unread highlight.
            if (_errorPanel.Visible) _transport.SetErrorHighlighted(false);
        };
        _transport.ThemeBtn.Click += (_, __) => ToggleDarkMode();
        _transport.MuteBtn.Click += (_, __) =>
        {
            _playback.Muted = !_playback.Muted;
            _transport.SetMuteGlyph(_playback.Muted);
            _settings.Muted = _playback.Muted;
        };
        _transport.Volume.ValueChanged += v =>
        {
            _playback.Volume = v;
            _settings.Volume = v;
            _transport.VolumeLabel.Text = v + "%";
        };
        _transport.VolumeLabel.Text = _settings.Volume + "%";
        _transport.Scrubber.SeekRequested += ms => _playback.TimeMs = ms;
        _transport.Scrubber.SeekPreview += ms => _transport.ElapsedLabel.Text = TransportBar.FormatTime(ms);

        Controls.Add(_videoHost);
        Controls.Add(_sidebar);
        Controls.Add(_transport);
        Controls.Add(_errorPanel);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        ApplyTheme();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _uiTimer.Tick += (_, __) => UpdateTransportFromPlayback();
        _uiTimer.Start();

        _positionSaveTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _positionSaveTimer.Tick += (_, __) => SavePositionState(withPositionMs: false);

        _memoryWatchdog = new System.Windows.Forms.Timer { Interval = 60_000 };
        _memoryWatchdog.Tick += (_, __) => CheckMemoryPressure();
        _memoryWatchdog.Start();
        WriteDiagnostic("app started: " + (DescribeProcessMemory()));

        _engineWatchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        _engineWatchdog.Tick += (_, __) =>
        {
            if (!_playback.WorkerLooksWedged(EngineWedgeThresholdMs)) return;
            // While the display is off a wedged worker bothers nobody, and a
            // rebuild attempted mid-transition could just wedge again. Wait
            // for the display to return; DoDisplayRecovery handles it then.
            if (_displayWasOff) return;
            // If rebuilds keep failing to produce actual playback, stop —
            // each abandoned engine leaks, and something deeper is wrong.
            if (_consecutiveEngineRebuilds >= MaxConsecutiveEngineRebuilds) return;
            RebuildPlaybackEngine("a playback call has been stuck for over 10 seconds", automatic: true, ignoreRateLimit: false);
        };
        _engineWatchdog.Start();

        FormClosing += OnFormClosing;
        RebuildRecentMenu();

        // Global low-level mouse hook. Gated by ClickIsOnOurWindow so it only
        // acts when the click actually targets our app — both when we're already
        // focused and when the click is bringing us to the foreground.
        _mouseHook.XButton1Pressed += () =>
        {
            if (IsHandleCreated && IsCursorOverOurWindow()) BeginInvoke(new Action(GoPrev));
        };
        _mouseHook.XButton2Pressed += () =>
        {
            if (IsHandleCreated && IsCursorOverOurWindow()) BeginInvoke(new Action(GoNext));
        };
        _mouseHook.LeftClickReleased += screenPt =>
        {
            if (!IsHandleCreated || !_videoHost.Visible) return;
            // Click must land inside our window AND specifically over the video region.
            if (!ClickIsOnOurWindow(screenPt)) return;
            var videoRect = _videoHost.RectangleToScreen(_videoHost.ClientRectangle);
            if (!videoRect.Contains(screenPt)) return;
            BeginInvoke(new Action(() => _playback.TogglePause()));
        };
        _mouseHook.Install();

        // Recover from system suspend/resume. Sleep can leave libvlc's D3D
        // video output in a broken state (black video) and/or bound to a
        // stale audio endpoint. On resume, re-issue playback on the current
        // file so libvlc rebuilds its video/audio pipeline. SystemEvents
        // fires on a non-UI thread, so marshal back before touching UI state.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        if (IsDisposed || !IsHandleCreated) return;
        // True suspend/resume takes the same debounced recovery path as a
        // display power-on — one coalesced pipeline rebuild once things have
        // settled. SystemEvents fires on a non-UI thread, so hop first.
        try { BeginInvoke(new Action(ScheduleDisplayRecovery)); } catch { }
    }

    private void WirePlayback(PlaybackController p)
    {
        // Every callback is gated on the raising controller STILL being the
        // current one. After RebuildPlaybackEngine swaps _playback, the
        // abandoned controller can still deliver already-queued events — a dying
        // vout's final EndReached/EncounteredError, a stale PipelineRecycled —
        // and acting on those would skip tracks in the shuffle or rebind the
        // VideoView to the wrong player. The capture of `p` plus the
        // ReferenceEquals check is the boundary equivalent of the controller's
        // internal ReferenceEquals(p, Player) guard. (No Unwire needed: a stale
        // controller's events are simply ignored until it is GC'd.)
        p.MediaEnded += () => { if (ReferenceEquals(_playback, p)) OnMediaEnded(); };
        p.MediaFailed += m => { if (ReferenceEquals(_playback, p)) OnMediaFailed(m); };
        p.StateChanged += () => { if (ReferenceEquals(_playback, p)) OnPlaybackStateChanged(); };
        p.PipelineRecycled += () => { if (ReferenceEquals(_playback, p)) OnPipelineRecycled(); };
    }

    private void OnPipelineRecycled()
    {
        if (IsDisposed) return;
        _playback.AttachTo(_videoHost.VideoView);
    }

    private void OnVideoViewHandleCreated(object? sender, EventArgs e)
    {
        // The VideoView's INITIAL handle creation happens during construction
        // (AttachTo reads view.Handle) BEFORE this handler is wired, so every
        // invocation we actually receive is a RE-creation: the current player's
        // Hwnd now points at a destroyed window. Re-bind to the live handle and
        // replay so the video re-embeds instead of opening its own window.
        // (The earlier "skip the first event" guard was an off-by-one — it
        // swallowed the first real recreation.) Re-binding is idempotent and
        // ScheduleDisplayRecovery no-ops when nothing is loaded, so this is safe
        // even on the rare chance the initial creation is observed here.
        if (IsDisposed) return;
        _playback.AttachTo(_videoHost.VideoView);
        ScheduleDisplayRecovery();
        StartDetachedWindowAdoptionSweep();
    }

    /// <summary>
    /// In-process equivalent of closing and reopening the app. Used when the
    /// playback worker is stuck inside a libvlc call that will never return —
    /// at that point no queued command (not even a rescue Recycle) can ever
    /// run, so the whole controller is abandoned and a fresh one built. The
    /// wedged controller's threads are background threads; they die with the
    /// process. Rate-limited so a repeatedly-wedging environment (e.g. GPU
    /// mid-reset) can't make us churn out leaked pipelines in a tight loop.
    /// </summary>
    private bool RebuildPlaybackEngine(string why, bool automatic, bool ignoreRateLimit)
    {
        long now = Environment.TickCount64;
        if (!ignoreRateLimit && now - _lastEngineRebuildTicks < EngineRebuildMinIntervalMs) return false;
        if (automatic && _automaticEngineRebuildsThisSession >= MaxAutomaticEngineRebuildsPerSession)
        {
            if (!_automaticEngineRebuildLimitLogged)
            {
                _automaticEngineRebuildLimitLogged = true;
                _errorPanel.Log("Automatic engine replacement paused after "
                    + MaxAutomaticEngineRebuildsPerSession
                    + " attempts this session to prevent runaway VLC memory growth. "
                    + "Use Player > Reset Player, or restart the app, if playback is still broken. "
                    + DescribeProcessMemory());
            }
            return false;
        }
        _errorPanel.Log("Playback engine unresponsive (" + why + ") — replacing it with a fresh one.");
        var old = _playback;
        long resumeMs = old.TimeMs;   // cached values — safe even when wedged
        int volume = old.Volume;
        bool muted = old.Muted;

        PlaybackController fresh;
        try { fresh = new PlaybackController(); }
        catch (Exception ex)
        {
            // Couldn't even build a new engine — keep limping with the old one.
            // (old is still _playback, so its guarded events still route here.)
            _errorPanel.Log("Engine rebuild failed: " + ex.Message);
            return false;
        }
        _lastEngineRebuildTicks = now;
        _consecutiveEngineRebuilds++;
        if (automatic) _automaticEngineRebuildsThisSession++;
        if (_consecutiveEngineRebuilds == MaxConsecutiveEngineRebuilds)
            _errorPanel.Log("Several engine rebuilds without playback succeeding - pausing automatic recovery. Use Player > Reset Player, or restart the app.");
        _errorPanel.Log(DescribeProcessMemory());

        // Publish the swap BEFORE wiring/attaching: the WirePlayback guards and
        // every stale event from `old` key off ReferenceEquals(_playback, ...),
        // so `_playback` must already point at `fresh`.
        _playback = fresh;
        WirePlayback(fresh);
        fresh.AttachTo(_videoHost.VideoView);
        try { old.Abandon(); } catch { }

        fresh.Volume = volume;
        fresh.Muted = muted;
        _transport.SetMuteGlyph(muted);
        if (_currentFullPath != null && File.Exists(_currentFullPath))
            fresh.PlayAt(_currentFullPath, resumeMs);
        StartDetachedWindowAdoptionSweep();
        return true;
    }

    // True when the window under the given screen point has our form as its
    // top-level owner. Works whether we're foreground or not — so a single
    // click that both activates us and targets the video panel is honored.
    private bool ClickIsOnOurWindow(System.Drawing.Point screenPt)
    {
        if (!IsHandleCreated) return false;
        var pt = new Win32.POINT { x = screenPt.X, y = screenPt.Y };
        var hwnd = Win32.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        return root == Handle;
    }

    private bool IsCursorOverOurWindow()
    {
        Win32.POINT pt;
        if (!Win32.GetCursorPos(out pt)) return false;
        return ClickIsOnOurWindow(new System.Drawing.Point(pt.x, pt.y));
    }


    private void TryLoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("RandVideoPlayer.app.ico");
            if (stream != null)
            {
                using var icon = new Icon(stream);
                Icon = (Icon)icon.Clone();
            }
        }
        catch { }
    }

    private void ApplyInitialBounds()
    {
        var b = _settings.WindowBounds;
        if (b != null && b.W > 300 && b.H > 200)
        {
            Bounds = new Rectangle(b.X, b.Y, b.W, b.H);
            if (b.Maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            Size = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterScreen;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Re-apply native chrome once all child handles exist.
        DarkChrome.ApplyTreeTheme(this, _theme.IsDark);
        // Re-bind now that the window handles are created and stable, so the
        // captured video HWND is the real one — not a transient handle from
        // construction time that WinForms may have since recreated.
        _playback.AttachTo(_videoHost.VideoView);
        if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
            OpenFolder(_settings.LastFolder);
        else
            PromptOpenFolder();
    }

    private void PromptOpenFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder to use as a playlist" };
        if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
            dlg.SelectedPath = _settings.LastFolder;
        if (dlg.ShowDialog(this) == DialogResult.OK && Directory.Exists(dlg.SelectedPath))
            OpenFolder(dlg.SelectedPath);
    }

    private void OpenFolder(string folder)
    {
        try
        {
            SavePositionState(withPositionMs: true);
            try { _playback.Stop(); } catch { }
            StopWatcher();
            DisposeDurationIndex();

            _library = new FolderLibrary(folder);
            _library.Rescan();
            _settings.MarkFolderUsed(folder);
            _settings.Save();
            RebuildRecentMenu();

            _favorites = PlaylistState.LoadFavorites(folder)?.Files ?? new List<string>();
            _favIndex = -1;
            _context = PlayContext.Shuffle;
            _shuffleReturnMs = 0;

            var existing = PlaylistState.LoadShuffle(folder);
            if (existing != null && ShuffleStillMatches(existing, _library))
            {
                _shuffle = existing;
                ReconcileWithFolder();
            }
            else
            {
                _shuffle = CreateFreshShuffle(folder, _library);
                PlaylistState.SaveShuffle(folder, _shuffle);
            }
            PruneFavoritesToLibrary();

            var pos = PlaylistState.LoadPosition(folder);
            _currentIndex = -1;
            _currentFullPath = null;
            _resumePositionMs = 0;
            _resumeApplied = true;
            if (pos != null && pos.CurrentIndex >= 0 && pos.CurrentIndex < _shuffle.Files.Count
                && !string.IsNullOrEmpty(pos.CurrentFileRelative)
                && string.Equals(_shuffle.Files[pos.CurrentIndex], pos.CurrentFileRelative, StringComparison.OrdinalIgnoreCase))
            {
                _currentIndex = pos.CurrentIndex;
                _currentFullPath = _library.ToFull(pos.CurrentFileRelative!);
                _resumePositionMs = Math.Max(0, pos.PositionMs);
                _resumeApplied = _resumePositionMs <= 0;
            }
            else if (_shuffle.Files.Count > 0)
            {
                _currentIndex = 0;
                _currentFullPath = _library.ToFull(_shuffle.Files[0]);
            }

            StartWatcher(folder);
            _durations = new DurationIndex(folder);
            _durations.Updated += OnDurationsUpdated;
            _durations.StartOrUpdate(_library.AlphaList);

            RefreshSidebar();
            _sidebar.EnsureCurrentVisible();
            UpdateNowPlayingLabel();
            UpdateWindowTitle();

            if (_currentIndex >= 0 && _currentFullPath != null && File.Exists(_currentFullPath))
                PlayShuffleAt(_currentIndex, _resumePositionMs, saveState: false);
        }
        catch (Exception ex)
        {
            _errorPanel.Log("OpenFolder failed: " + ex.Message);
            _errorPanel.Visible = true;
        }
    }

    private void OnDurationsUpdated()
    {
        if (IsDisposed) return;
        void apply()
        {
            if (_durations == null) return;
            _sidebar.SetStats(_durations.FileCount, _durations.TotalDurationMs,
                              _durations.Scanning, _durations.ScannedCount);
        }
        if (InvokeRequired) BeginInvoke(new Action(apply));
        else apply();
    }

    private void DisposeDurationIndex()
    {
        var d = _durations;
        _durations = null;
        if (d == null) return;
        try { d.Updated -= OnDurationsUpdated; } catch { }
        try { d.Dispose(); } catch { }
    }

    private static bool ShuffleStillMatches(ShuffleFile sf, FolderLibrary lib)
    {
        foreach (var rel in sf.Files)
        {
            var full = lib.ToFull(rel);
            if (File.Exists(full)) return true;
        }
        return false;
    }

    private static ShuffleFile CreateFreshShuffle(string folder, FolderLibrary lib)
    {
        uint seed = ShuffleEngine.MakeSeed(folder);
        var rels = lib.AlphaRelative().ToList();
        var shuffled = ShuffleEngine.Shuffle(rels, seed);
        return new ShuffleFile { Seed = seed, CreatedUtc = DateTime.UtcNow, Files = shuffled };
    }

    private void ReconcileWithFolder()
    {
        if (_shuffle == null || _library == null) return;
        var actualRel = new HashSet<string>(_library.AlphaRelative(), StringComparer.OrdinalIgnoreCase);
        var shuffleSet = new HashSet<string>(_shuffle.Files, StringComparer.OrdinalIgnoreCase);

        var removed = _shuffle.Files.Where(r => !actualRel.Contains(r)).ToList();
        foreach (var r in removed) RemoveFromShuffle(r, logWhy: null);

        var added = actualRel.Where(r => !shuffleSet.Contains(r)).ToList();
        foreach (var r in added)
        {
            int afterIdx = Math.Max(_currentIndex, -1);
            int insertAt = ShuffleEngine.PickInsertionIndex(_shuffle.Seed, r, afterIdx, _shuffle.Files.Count);
            _shuffle.Files.Insert(insertAt, r);
            if (insertAt <= _currentIndex) _currentIndex++;
        }
        if (removed.Count > 0 || added.Count > 0)
            PlaylistState.SaveShuffle(_library.RootFolder, _shuffle);
    }

    private void RemoveFromShuffle(string relativePath, string? logWhy)
    {
        if (_shuffle == null || _library == null) return;
        int idx = _shuffle.Files.FindIndex(s => string.Equals(s, relativePath, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        // Only "current" in the sense that matters if the shuffle list is what
        // is actually driving playback right now.
        bool wasCurrent = idx == _currentIndex && _context == PlayContext.Shuffle;
        _shuffle.Files.RemoveAt(idx);
        if (idx < _currentIndex) _currentIndex--;
        else if (idx == _currentIndex)
        {
            _currentIndex = Math.Min(_currentIndex, _shuffle.Files.Count - 1);
            if (_context == PlayContext.Shuffle)
                _currentFullPath = _currentIndex >= 0 ? _library.ToFull(_shuffle.Files[_currentIndex]) : null;
            else
                _shuffleReturnMs = 0;   // that playhead belonged to the file we just dropped
        }
        if (logWhy != null) _errorPanel.Log(logWhy);
        if (wasCurrent && _currentIndex >= 0)
            PlayShuffleAt(_currentIndex);
    }

    // ---- Playback contexts ---------------------------------------------------
    // Every route into playback goes through one of PlayShuffleAt / PlayOneShot /
    // PlayFavoriteAt so that _context, _currentIndex and _favIndex can never
    // disagree with what is on screen.

    private void StartPlayback(string fullPath, long resumeMs, bool saveState)
    {
        // Anything that starts a real file ends an audio preview excursion.
        if (_audioPreviewActive) EndAudioPreviewState();
        _currentFullPath = fullPath;
        _resumePositionMs = Math.Max(0, resumeMs);
        _resumeApplied = _resumePositionMs <= 0;
        _playback.Play(fullPath);
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
        if (saveState) SavePositionState(withPositionMs: false);
        UpdateNowPlayingLabel();
        UpdateWindowTitle();
        _sidebar.HighlightPath(fullPath);
    }

    private void PlayShuffleAt(int idx, long resumeMs = 0, bool saveState = true)
    {
        if (_library == null || _shuffle == null) return;
        if (idx < 0 || idx >= _shuffle.Files.Count) return;
        _context = PlayContext.Shuffle;
        _favIndex = -1;
        _shuffleReturnMs = 0;
        _currentIndex = idx;
        StartPlayback(_library.ToFull(_shuffle.Files[idx]), resumeMs, saveState);
    }

    // Mark where the shuffle list was so an excursion can hand control back to
    // the exact moment it interrupted. Only the FIRST departure marks it —
    // hopping favorite-to-favorite must not overwrite the original spot.
    private void MarkShuffleReturnPoint()
    {
        if (_context != PlayContext.Shuffle) return;
        _shuffleReturnMs = _playback.IsPlaying ? _playback.TimeMs : 0;
    }

    // Search-tab play: this file and nothing else, then back to the shuffle.
    private void PlayOneShot(string fullPath)
    {
        MarkShuffleReturnPoint();
        _context = PlayContext.OneShot;
        _favIndex = -1;
        StartPlayback(fullPath, 0, saveState: true);
    }

    private void PlayFavoriteAt(int i)
    {
        if (_library == null || i < 0 || i >= _favorites.Count) return;
        MarkShuffleReturnPoint();
        _context = PlayContext.Favorites;
        _favIndex = i;
        StartPlayback(_library.ToFull(_favorites[i]), 0, saveState: true);
    }

    private void ReturnToShuffle()
    {
        _context = PlayContext.Shuffle;
        _favIndex = -1;
        if (_library == null || _shuffle == null || _shuffle.Files.Count == 0) return;

        long resume = _shuffleReturnMs;
        int idx = _currentIndex;
        if (idx < 0 || idx >= _shuffle.Files.Count) { idx = 0; resume = 0; }
        var full = _library.ToFull(_shuffle.Files[idx]);
        if (!File.Exists(full))
        {
            // The track we were going to hand back to is gone; carry on down the list.
            _currentIndex = idx;
            _shuffleReturnMs = 0;
            GoNext();
            return;
        }
        PlayShuffleAt(idx, resume);
    }

    // Re-load the file that is already current because its bytes changed on
    // disk (the cut tool swapping in a trimmed version). Deliberately leaves
    // _context/_currentIndex/_favIndex alone — nothing about the queue moved.
    private void ReloadCurrentFile(string path) => StartPlayback(path, 0, saveState: false);

    private void PlayFromSidebar(string fullPath)
    {
        if (_library == null || _shuffle == null) return;
        switch (_sidebar.Mode)
        {
            case Sidebar.ViewMode.Favorites:
            {
                int i = FavoriteIndexOf(fullPath);
                if (i >= 0) PlayFavoriteAt(i); else PlayOneShot(fullPath);
                break;
            }
            case Sidebar.ViewMode.ShuffleOrder:
            {
                var rel = _library.ToRelative(fullPath);
                int i = _shuffle.Files.FindIndex(s => string.Equals(s, rel, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) PlayShuffleAt(i); else PlayOneShot(fullPath);
                break;
            }
            default:
                // Search is stateless: play exactly this, then resume the shuffle.
                PlayOneShot(fullPath);
                break;
        }
    }

    private void GoPrev()
    {
        if (_shuffle == null || _library == null) return;
        if (_context == PlayContext.OneShot) { ReturnToShuffle(); return; }
        if (_context == PlayContext.Favorites)
        {
            if (_favIndex > 0) PlayFavoriteAt(_favIndex - 1);
            else SystemSounds.Beep();
            return;
        }
        if (_shuffle.Files.Count == 0) return;
        if (_currentIndex <= 0) { SystemSounds.Beep(); return; }
        PlayShuffleAt(_currentIndex - 1);
    }

    private void GoNext()
    {
        if (_shuffle == null || _library == null) return;
        if (_context == PlayContext.OneShot) { ReturnToShuffle(); return; }
        if (_context == PlayContext.Favorites)
        {
            if (_favIndex + 1 < _favorites.Count) PlayFavoriteAt(_favIndex + 1);
            else ReturnToShuffle();
            return;
        }
        if (_shuffle.Files.Count == 0) return;
        if (_currentIndex + 1 < _shuffle.Files.Count)
        {
            PlayShuffleAt(_currentIndex + 1);
        }
        else
        {
            ReshuffleInternal(antiRepeat: true, playFirst: true);
        }
    }

    // Ctrl+click on Prev/Next: jump to the song boundary closest to ~1 hour
    // away (in either direction), measured by accumulated cached durations.
    // Songs whose duration hasn't been scanned yet count as 0, so during the
    // initial scan this can land further than expected.
    private void JumpByApproxHour(bool forward)
    {
        if (_shuffle == null || _library == null || _durations == null) return;
        if (_shuffle.Files.Count == 0 || _currentIndex < 0) return;

        const long ONE_HOUR_MS = 60L * 60L * 1000L;
        long DurAt(int i) => _durations.GetDurationMs(_library.ToFull(_shuffle.Files[i]));

        int n = _shuffle.Files.Count;
        int target = -1;
        long bestDiff = long.MaxValue;
        long cumulative = 0;

        if (forward)
        {
            // Cumulative = offset from start of current song to start of song t.
            for (int t = _currentIndex + 1; t < n; t++)
            {
                cumulative += DurAt(t - 1);
                long diff = Math.Abs(cumulative - ONE_HOUR_MS);
                if (target < 0 || diff < bestDiff) { bestDiff = diff; target = t; }
                if (cumulative >= ONE_HOUR_MS) break;
            }
        }
        else
        {
            // Cumulative = sum of durations of songs t..currentIndex-1 (rewound time).
            for (int t = _currentIndex - 1; t >= 0; t--)
            {
                cumulative += DurAt(t);
                long diff = Math.Abs(cumulative - ONE_HOUR_MS);
                if (target < 0 || diff < bestDiff) { bestDiff = diff; target = t; }
                if (cumulative >= ONE_HOUR_MS) break;
            }
        }

        if (target < 0) { SystemSounds.Beep(); return; }
        // An hour-jump is inherently a shuffle-list move, so it also ends any
        // favorites/search excursion.
        PlayShuffleAt(target);
    }

    // ---- Favorites -----------------------------------------------------------

    private static bool RelEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private int FavoriteIndexOf(string fullPath)
    {
        if (_library == null) return -1;
        var rel = _library.ToRelative(fullPath);
        return _favorites.FindIndex(f => RelEquals(f, rel));
    }

    private bool IsFavoritePath(string fullPath) => FavoriteIndexOf(fullPath) >= 0;

    private void SaveFavorites()
    {
        if (_library == null) return;
        try { PlaylistState.SaveFavorites(_library.RootFolder, new FavoritesFile { Files = new List<string>(_favorites) }); }
        catch (Exception ex) { _errorPanel.Log("Favorites save failed: " + ex.Message); }
    }

    private void AddFavorite(string fullPath)
    {
        if (_library == null) return;
        var rel = _library.ToRelative(fullPath);
        if (_favorites.Any(f => RelEquals(f, rel))) return;
        _favorites.Add(rel);
        SaveFavorites();
        RefreshSidebar();
    }

    private void RemoveFavorite(string fullPath)
    {
        if (_library == null) return;
        RemoveFavoriteRel(_library.ToRelative(fullPath));
    }

    private void RemoveFavoriteRel(string rel)
    {
        int i = _favorites.FindIndex(f => RelEquals(f, rel));
        if (i < 0) return;
        _favorites.RemoveAt(i);
        if (_context == PlayContext.Favorites)
        {
            // Leave _favIndex pointing one before the slot that just opened up,
            // so Next continues with whatever shifted into it.
            if (i <= _favIndex) _favIndex--;
        }
        SaveFavorites();
        RefreshSidebar();
    }

    // newIndex is the slot the row should occupy in the list as it looked
    // BEFORE the move, which is what the drop indicator was drawn against.
    private void MoveFavorite(string fullPath, int newIndex)
    {
        if (_library == null) return;
        var rel = _library.ToRelative(fullPath);
        int old = _favorites.FindIndex(f => RelEquals(f, rel));
        if (old < 0) return;
        if (newIndex == old || newIndex == old + 1) return;   // dropped where it already is

        string? playingRel = (_context == PlayContext.Favorites && _favIndex >= 0 && _favIndex < _favorites.Count)
            ? _favorites[_favIndex] : null;

        _favorites.RemoveAt(old);
        if (newIndex > old) newIndex--;
        newIndex = Math.Clamp(newIndex, 0, _favorites.Count);
        _favorites.Insert(newIndex, rel);

        // Follow the file that is playing rather than the slot number.
        if (playingRel != null) _favIndex = _favorites.FindIndex(f => RelEquals(f, playingRel));

        SaveFavorites();
        RefreshSidebar();
    }

    // Drop favorites whose files are no longer in the folder. Only called where
    // we have just rescanned, so a missing entry really is gone.
    private void PruneFavoritesToLibrary()
    {
        if (_library == null || _favorites.Count == 0) return;
        var actual = new HashSet<string>(_library.AlphaRelative(), StringComparer.OrdinalIgnoreCase);
        int before = _favorites.Count;
        _favorites.RemoveAll(f => !actual.Contains(f));
        if (_favorites.Count != before) SaveFavorites();
    }

    private void OnMediaEnded()
    {
        if (IsDisposed) return;
        // A preview clip running out means "preview over", not "track over" —
        // advancing here would silently move the shuffle on.
        BeginInvoke(new Action(() => { if (_audioPreviewActive) StopAudioPreview(); else GoNext(); }));
    }

    private void OnMediaFailed(string message)
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            _errorPanel.Log(message);
            _consecutivePlaybackFailures++;
            long now = Environment.TickCount64;
            bool manyInARow = _consecutivePlaybackFailures >= PlaybackFailuresBeforeEscalation;
            bool escalationAllowed = now - _lastPlaybackEscalationTicks >= PlaybackEscalationMinIntervalMs;
            if (manyInARow && escalationAllowed && _currentFullPath != null)
            {
                // Files failing back-to-back means the PIPELINE is dead, not the
                // files — a black recovery that never reached Playing. Rebuild
                // rather than skipping forever on a black screen.
                _errorPanel.Log("Repeated playback failures — rebuilding the pipeline instead of skipping.");
                bool recoveryStarted;
                bool workerWedged = _playback.WorkerLooksWedged(EngineWedgeThresholdMs);
                if (workerWedged)
                    recoveryStarted = RebuildPlaybackEngine("repeated playback failures with the worker stuck", automatic: true, ignoreRateLimit: false);
                else
                    recoveryStarted = TryRecyclePlaybackPipeline("repeated playback failures", automatic: true);

                if (recoveryStarted)
                {
                    _lastPlaybackEscalationTicks = now;
                    _consecutivePlaybackFailures = 0;
                }
                else if (!workerWedged)
                {
                    GoNext();
                }
            }
            else
            {
                GoNext();
            }
        }));
    }

    private void OnPlaybackStateChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            // Real playback succeeded — the engine is healthy, so re-arm the
            // bounded auto-rebuild allowance and clear the failure streak.
            if (_playback.IsPlaying)
            {
                _consecutiveEngineRebuilds = 0;
                _consecutivePlaybackFailures = 0;
            }
            _transport.SetPlayPauseGlyph(_playback.IsPlaying);
            if (!_resumeApplied && _playback.IsPlaying && _resumePositionMs > 0)
            {
                _playback.TimeMs = _resumePositionMs;
                _resumePositionMs = 0;
                _resumeApplied = true;
            }
        }));
    }

    private void UpdateTransportFromPlayback()
    {
        _transport.Scrubber.LengthMs = _playback.LengthMs;
        _transport.Scrubber.TimeMs = _playback.TimeMs;
        _transport.ElapsedLabel.Text = TransportBar.FormatTime(_playback.TimeMs);
        _transport.TotalLabel.Text = TransportBar.FormatTime(_playback.LengthMs);
    }

    private void ReshuffleWithConfirm()
    {
        if (_shuffle == null || _shuffle.Files.Count == 0) return;
        var r = MessageBox.Show(this,
            "Reshuffle now? The current shuffle order will be lost.",
            "Reshuffle", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return;
        ReshuffleInternal(antiRepeat: true, playFirst: true);
    }

    private void ReshuffleInternal(bool antiRepeat, bool playFirst)
    {
        if (_library == null || _shuffle == null) return;
        string? justPlayedRel = _currentFullPath != null ? _library.ToRelative(_currentFullPath) : null;
        uint seed = ShuffleEngine.MakeSeed(_library.RootFolder);
        var rels = _library.AlphaRelative().ToList();
        var newOrder = antiRepeat
            ? ShuffleEngine.ReshuffleAtEnd<string>(rels, seed, justPlayedRel)
            : ShuffleEngine.Shuffle(rels, seed);
        _shuffle = new ShuffleFile { Seed = seed, CreatedUtc = DateTime.UtcNow, Files = newOrder };
        PlaylistState.SaveShuffle(_library.RootFolder, _shuffle);
        _currentIndex = _shuffle.Files.Count > 0 ? 0 : -1;
        // A reshuffle invalidates the spot any excursion was going to return to.
        _shuffleReturnMs = 0;
        if (_context == PlayContext.Shuffle)
            _currentFullPath = _currentIndex >= 0 ? _library.ToFull(_shuffle.Files[_currentIndex]) : null;
        RefreshSidebar();
        _sidebar.EnsureCurrentVisible();
        UpdateNowPlayingLabel();
        UpdateWindowTitle();
        if (playFirst && _currentIndex >= 0)
            PlayShuffleAt(_currentIndex);
        else
            SavePositionState(withPositionMs: false);
    }

    private void HandleDeleteFromSidebar(string fullPath)
    {
        if (_library == null) return;
        var r = MessageBox.Show(this,
            $"Send this file to the Recycle Bin?\n\n{fullPath}",
            "Delete File", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (r != DialogResult.OK) return;

        bool isPlaying = string.Equals(_currentFullPath, fullPath, StringComparison.OrdinalIgnoreCase);
        if (isPlaying)
        {
            // Move playback off the doomed file first — libvlc keeps an open
            // file handle on whatever it's playing, and SHFileOperation will
            // block the UI thread indefinitely waiting on that handle. The
            // atomic Player.Play swap inside GoNext is what releases the old
            // media; Stop alone can race because it's enqueued on a worker.
            if (_shuffle != null && _shuffle.Files.Count > 1)
            {
                GoNext();
            }
            else
            {
                try { _playback.Stop(); } catch { }
                _currentFullPath = null;
                UpdateNowPlayingLabel();
                UpdateWindowTitle();
            }
            // The play/stop was enqueued on the VLC worker; defer the recycle
            // until that queued work has actually finished executing.
            _playback.RunAfterPendingWork(() => PerformRecycle(fullPath));
        }
        else
        {
            PerformRecycle(fullPath);
        }
    }

    private void PerformRecycle(string fullPath)
    {
        if (!ShellOps.SendToRecycleBin(fullPath))
        {
            _errorPanel.Log("Recycle Bin delete failed: " + fullPath);
            return;
        }
        // Don't wait for the watcher debounce to notice — the row should go now.
        if (_library != null) RemoveFavoriteRel(_library.ToRelative(fullPath));
    }

    // ---- In-app cut tool -----------------------------------------------------
    // Opens the pop-out trim window. Preview reuses the MAIN player (so scrubbing
    // rides the hardened libvlc pipeline); this window only collects In/Out and a
    // lossless/re-encode choice and hands a CutRequest back to PerformCut.

    private void OpenCutWindow(string path)
    {
        if (_library == null) return;
        if (!Ffmpeg.IsAvailable)
        {
            MessageBox.Show(this,
                "ffmpeg was not found on this system.\n\nInstall it (e.g. `winget install Gyan.FFmpeg`) to enable in-app cutting.",
                "Cut", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "File not found:\n" + path, "Cut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_cutWindow != null && !_cutWindow.IsDisposed)
        {
            _cutWindow.Activate();
            return;
        }

        // Preview reuses the main player, so the target must be what's loaded.
        // Route it through the tab it was invoked from so a cut started from
        // Search doesn't quietly hijack the shuffle position.
        if (!string.Equals(_currentFullPath, path, StringComparison.OrdinalIgnoreCase))
            PlayFromSidebar(path);

        double fps = 30.0;
        var info = Ffmpeg.Probe(path);
        if (info != null && info.Fps > 1) fps = info.Fps;

        var win = new CutWindow(
            path, Path.GetFileName(path), _theme, fps,
            getTimeMs: () => _playback.TimeMs,
            getLengthMs: () => _playback.LengthMs,
            seekMs: ms => _playback.TimeMs = ms,
            togglePause: () => _playback.TogglePause(),
            getIsPlaying: () => _playback.IsPlaying);
        _cutWindow = win;
        win.CutConfirmed += req => PerformCut(path, req, win);
        win.FormClosed += (_, __) => { if (ReferenceEquals(_cutWindow, win)) _cutWindow = null; };
        win.Show(this);

        // Keyframe probing reads the whole packet index — do it off-thread and
        // push the result into the window when it's ready.
        Task.Run(() =>
        {
            var kf = Ffmpeg.GetKeyframes(path);
            try { win.BeginInvoke(new Action(() => { if (!win.IsDisposed) win.SetKeyframes(kf); })); } catch { }
        });
    }

    private void PerformCut(string path, CutRequest req, CutWindow win)
    {
        if (_library == null) return;

        string modeDesc = req.Reencode
            ? "Frame-accurate (re-encodes the selection — slight quality loss)."
            : "Lossless (stream copy — zero quality loss; start snaps to a keyframe).";
        var confirm = MessageBox.Show(this,
            "Replace the original with the cut version?\n\n" + Path.GetFileName(path) + "\n\n" +
            "Mode: " + modeDesc + "\n\nThe original is moved to the Recycle Bin as a backup.",
            "Cut & Save", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        double inSec = req.InMs / 1000.0;
        double outSec = req.OutMs / 1000.0;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        string temp = Path.Combine(dir, name + ".rvpcut-tmp" + ext);

        win.SetBusy(true, "Preparing…");

        Task.Run(() =>
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            void Prog(double f) => win.PostToUi(() => win.ReportProgress(f));

            bool ok;
            string err;
            if (req.Reencode)
            {
                var pinfo = Ffmpeg.Probe(path);
                string pix = pinfo?.PixFmt ?? "yuv420p";
                ok = Ffmpeg.CutReencode(path, inSec, outSec, pix, temp, Prog, CancellationToken.None, out err);
            }
            else
            {
                var kfs = Ffmpeg.GetKeyframes(path);
                double kfStart = Ffmpeg.KeyframeAtOrBefore(kfs, inSec);
                double dur = Math.Max(0.05, outSec - kfStart);
                ok = Ffmpeg.CutLossless(path, kfStart, dur, temp, Prog, CancellationToken.None, out err);
            }

            if (ok)
            {
                var vinfo = Ffmpeg.Probe(temp);
                if (vinfo == null || !vinfo.HasVideo || vinfo.DurationSec < 0.05)
                {
                    ok = false;
                    err = "Output verification failed (no video / zero duration).";
                }
            }

            if (!ok)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                win.PostToUi(() => win.ReportDone(false, "Failed: " + Trunc(err)));
                return;
            }

            try { BeginInvoke(new Action(() => FinishJobSwap(path, temp, win, ".rvpcut-bak", "Cut"))); } catch { }
        });
    }

    // The output is ready and verified. Release libvlc's handle on the file (it
    // keeps the currently-playing file open), THEN swap it in. Mirrors the delete
    // path: Stop, wait for the queued work to drain, then touch the file.
    // Shared by the cut tool and the audio tool — only the backup suffix and the
    // log wording differ.
    private void FinishJobSwap(string path, string temp, IMediaJobUi ui, string bakSuffix, string label)
    {
        if (!ui.IsDisposed) ui.SetBusy(true, "Saving…");
        bool isCurrent = string.Equals(_currentFullPath, path, StringComparison.OrdinalIgnoreCase);
        if (isCurrent)
        {
            try { _playback.Stop(); } catch { }
            _playback.RunAfterPendingWork(() => BackgroundSwap(path, temp, ui, wasCurrent: true, bakSuffix, label));
        }
        else
        {
            BackgroundSwap(path, temp, ui, wasCurrent: false, bakSuffix, label);
        }
    }

    // The rename/move can briefly hit a sharing violation while libvlc finishes
    // releasing the handle, so the retry loop runs OFF the UI thread. Only the
    // reload + status report marshal back.
    private void BackgroundSwap(string path, string temp, IMediaJobUi ui, bool wasCurrent,
                                string bakSuffix, string label)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        string bak = Path.Combine(dir, name + bakSuffix + ext);

        Task.Run(() =>
        {
            bool ok = SwapInPlace(path, temp, bak, out string err);
            bool recycledBak = ok && File.Exists(bak) && ShellOps.SendToRecycleBin(bak);

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        if (!recycledBak) _errorPanel.Log(label + ": backup not sent to Recycle Bin: " + bak);
                        if (wasCurrent) ReloadCurrentFile(path);
                        _errorPanel.Log(label + " saved (original in Recycle Bin): " + Path.GetFileName(path));
                        if (!ui.IsDisposed) ui.ReportDone(true, "Saved.");
                    }
                    else
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                        if (wasCurrent && File.Exists(path)) ReloadCurrentFile(path);
                        if (!ui.IsDisposed) ui.ReportDone(false, "Save failed: " + Trunc(err));
                    }
                }));
            }
            catch { }
        });
    }

    // original -> backup, temp -> original. On failure the original is restored
    // from the backup so the library file is never lost.
    private static bool SwapInPlace(string original, string temp, string backup, out string err)
    {
        err = "";
        try { if (File.Exists(backup)) File.Delete(backup); } catch { }

        if (!TryMoveWithRetry(original, backup, out err)) return false;

        try { File.Move(temp, original); }
        catch (Exception ex)
        {
            err = ex.Message;
            try { if (!File.Exists(original) && File.Exists(backup)) File.Move(backup, original); } catch { }
            return false;
        }
        return true;
    }

    private static bool TryMoveWithRetry(string from, string to, out string err)
    {
        err = "";
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { File.Move(from, to); return true; }
            catch (IOException ex) { err = ex.Message; Thread.Sleep(100); }
            catch (UnauthorizedAccessException ex) { err = ex.Message; Thread.Sleep(100); }
        }
        return false;
    }

    // ---- In-app audio mastering ---------------------------------------------
    // Peak / loudness normalization plus multiband compression, so a quiet track
    // can be brought in line with the rest of the library without a round trip
    // through an external editor. The VIDEO stream is copied untouched — only the
    // audio is re-encoded — so this is fast and visually lossless. The result
    // goes through exactly the same verify / backup / Recycle-Bin swap as the
    // cut tool (FinishJobSwap).

    private void OpenAudioWindow(string path)
    {
        if (!Ffmpeg.IsAvailable)
        {
            MessageBox.Show(this,
                "ffmpeg was not found on this system.\n\nInstall it (e.g. `winget install Gyan.FFmpeg`) to enable in-app audio processing.",
                "Audio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "File not found:\n" + path, "Audio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_audioWindow != null && !_audioWindow.IsDisposed)
        {
            _audioWindow.Activate();
            return;
        }

        var info = Ffmpeg.Probe(path);
        if (info != null && !info.HasAudio)
        {
            MessageBox.Show(this, "This file has no audio track to process.", "Audio",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (info != null && info.AudioStreams > 1)
        {
            var multi = MessageBox.Show(this,
                "This file has " + info.AudioStreams + " audio tracks.\n\n" +
                "Processing keeps only the FIRST one; the others would be dropped. Continue?",
                "Audio", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (multi != DialogResult.OK) return;
        }

        // Preview plays through the main player, so the target has to be what is
        // loaded — the same rule the cut tool follows.
        if (!string.Equals(_currentFullPath, path, StringComparison.OrdinalIgnoreCase))
            PlayFromSidebar(path);

        var win = new AudioWindow(path, Path.GetFileName(path), _theme, _audioFx.Clone(), info,
                                  getTimeMs: () => _playback.TimeMs);
        _audioWindow = win;
        win.SettingsChanged += s => { _audioFx = s; _settings.AudioFx = ToPrefs(s); };
        win.ApplyConfirmed += req => PerformAudioProcess(path, req, win);
        win.PreviewRequested += req => StartAudioPreview(path, req, win);
        win.PreviewStopRequested += StopAudioPreview;
        win.FormClosed += (_, __) =>
        {
            if (ReferenceEquals(_audioWindow, win)) _audioWindow = null;
            if (_audioPreviewActive) StopAudioPreview();
        };
        win.Show(this);
    }

    private void PerformAudioProcess(string path, AudioRequest req, AudioWindow win)
    {
        if (!req.Settings.ChangesAnything) return;
        // The original file — not a preview clip — has to be what is loaded and
        // what gets replaced.
        if (_audioPreviewActive) StopAudioPreview();

        var confirm = MessageBox.Show(this,
            "Replace the original with the processed version?\n\n" + Path.GetFileName(path) + "\n\n" +
            DescribeAudioJob(req.Settings) + "\n\n" +
            "Video is copied untouched; only the audio is re-encoded.\n" +
            "The original is moved to the Recycle Bin as a backup.",
            "Apply audio processing", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        string temp = Path.Combine(dir, name + ".rvpaudio-tmp" + ext);

        var settings = req.Settings;
        var measured = req.Measured;
        win.SetBusy(true, "Preparing…");

        Task.Run(() =>
        {
            void Prog(double f) => win.PostToUi(() => win.ReportProgress(f));

            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            var info = Ffmpeg.Probe(path);
            double dur = info?.DurationSec ?? 0;

            // The window normally has a fresh measurement already; re-measure if it
            // does not, because the normalizer's second pass depends on it.
            if (measured == null || !measured.Ok)
            {
                win.PostToUi(() => win.SetBusy(true, "Analyzing…"));
                measured = AudioFx.Analyze(path, settings, dur, f => Prog(f * 0.35), CancellationToken.None);
                if (!measured.Ok && settings.Normalize != AudioFx.NormalizeMode.None)
                {
                    string why = measured.Error ?? "analysis failed";
                    win.PostToUi(() => win.ReportDone(false, "Failed: " + Trunc(why)));
                    return;
                }
            }

            bool ok = AudioFx.Apply(path, temp, settings, measured, info, dur,
                                    f => Prog(0.35 + f * 0.65), CancellationToken.None, out string err);

            if (ok)
            {
                var v = Ffmpeg.Probe(temp);
                bool sane = v != null
                            && v.HasAudio
                            && (info == null || !info.HasVideo || v.HasVideo)
                            && v.DurationSec > 0.05
                            && (dur <= 0 || Math.Abs(v.DurationSec - dur) <= Math.Max(1.0, dur * 0.02));
                if (!sane)
                {
                    ok = false;
                    err = "Output verification failed (missing stream or unexpected duration).";
                }
            }

            if (!ok)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                win.PostToUi(() => win.ReportDone(false, "Failed: " + Trunc(err)));
                return;
            }

            try { BeginInvoke(new Action(() => FinishJobSwap(path, temp, win, ".rvpaudio-bak", "Audio"))); } catch { }
        });
    }

    private static string DescribeAudioJob(AudioFx.Settings s)
    {
        var parts = new List<string>();
        if (s.HighPass) parts.Add("rumble filter");
        if (s.Compressor != AudioFx.CompressorStyle.None) parts.Add(AudioFx.StyleName(s.Compressor));
        parts.Add(s.Normalize switch
        {
            AudioFx.NormalizeMode.Peak => "peak normalize to " + s.PeakTargetDb.ToString("0.##", CultureInfo.InvariantCulture) + " dBFS",
            AudioFx.NormalizeMode.Loudness => "loudness normalize to " + s.LoudnessTargetLufs.ToString("0.#", CultureInfo.InvariantCulture) + " LUFS",
            AudioFx.NormalizeMode.Dynamic => "dynamic leveller",
            _ => "no level change",
        });
        if (s.Limiter) parts.Add("limiter at " + s.LimiterCeilingDb.ToString("0.##", CultureInfo.InvariantCulture) + " dBFS");
        return string.Join("  ->  ", parts);
    }

    // ---- audio preview excursion --------------------------------------------

    private void StartAudioPreview(string path, AudioRequest req, AudioWindow win)
    {
        if (_audioPreviewActive) StopAudioPreview();

        var info = Ffmpeg.Probe(path);
        double dur = info?.DurationSec ?? 0;
        const double PreviewSec = 20.0;
        double start = req.StartMs / 1000.0;
        // Back off from the very end so a preview started near the outro still has
        // something to play.
        if (dur > PreviewSec) start = Math.Clamp(start, 0, dur - PreviewSec);
        else start = 0;

        string file = Path.Combine(Path.GetTempPath(),
            "rvp-audio-preview-" + Environment.ProcessId + "-" + (++_audioPreviewSeq) + ".mp4");

        var settings = req.Settings;
        var measured = req.Measured;
        long backTo = req.StartMs;
        win.SetBusy(true, "Rendering preview…");

        Task.Run(() =>
        {
            bool ok = AudioFx.RenderPreview(path, file, start, PreviewSec, settings, measured, info,
                                            CancellationToken.None, out string err);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (win.IsDisposed) { DeleteTempLater(file); return; }
                    if (!ok || !File.Exists(file))
                    {
                        DeleteTempLater(file);
                        win.NotifyPreviewEnded();
                        win.ReportDone(false, "Preview failed: " + Trunc(err));
                        return;
                    }
                    _audioPreviewActive = true;
                    _audioPreviewFile = file;
                    _audioPreviewReturnMs = backTo;
                    // A resume-seek still pending from startup would land past the
                    // end of a 20-second clip; the excursion supplies its own start.
                    _resumePositionMs = 0;
                    _resumeApplied = true;
                    _playback.PlayAt(file, 0);
                    win.SetBusy(false, "Previewing " + (int)PreviewSec + "s from " + FormatMs((long)(start * 1000))
                                       + " — processed. Press \"Back to original\" to return.");
                }));
            }
            catch { DeleteTempLater(file); }
        });
    }

    private void StopAudioPreview()
    {
        if (!_audioPreviewActive) return;
        long back = _audioPreviewReturnMs;
        string? path = _currentFullPath;
        EndAudioPreviewState();
        if (path != null && File.Exists(path)) StartPlayback(path, back, saveState: false);
    }

    // Clears the excursion flags and disposes the temp clip. Deliberately does NOT
    // touch playback: StartPlayback calls this when the user navigates away
    // mid-preview, at which point it has already decided what to play.
    private void EndAudioPreviewState()
    {
        _audioPreviewActive = false;
        string? file = _audioPreviewFile;
        _audioPreviewFile = null;
        if (_audioWindow != null && !_audioWindow.IsDisposed) _audioWindow.NotifyPreviewEnded();
        if (file != null) DeleteTempLater(file);
    }

    // libvlc may still be releasing its handle on the clip we just moved off, so
    // retry off the UI thread rather than leaking a file on the first failure.
    private static void DeleteTempLater(string file)
    {
        Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                try { if (!File.Exists(file)) return; File.Delete(file); return; }
                catch { Thread.Sleep(150); }
            }
        });
    }

    private static string FormatMs(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0:0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:0}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
    }

    // ---- audio settings <-> on-disk prefs ------------------------------------

    private static AudioFx.Settings FromPrefs(AudioFxPrefs p)
    {
        var s = new AudioFx.Settings
        {
            PeakTargetDb = p.PeakTargetDb,
            LoudnessTargetLufs = p.LoudnessTargetLufs,
            TruePeakDb = p.TruePeakDb,
            LoudnessRangeLu = p.LoudnessRangeLu,
            HighPass = p.HighPass,
            Limiter = p.Limiter,
            LimiterCeilingDb = p.LimiterCeilingDb,
            AudioBitrateKbps = p.AudioBitrateKbps,
        };
        if (Enum.TryParse<AudioFx.NormalizeMode>(p.Normalize, true, out var n)) s.Normalize = n;
        if (Enum.TryParse<AudioFx.CompressorStyle>(p.Compressor, true, out var c)) s.Compressor = c;
        return s;
    }

    private static AudioFxPrefs ToPrefs(AudioFx.Settings s) => new()
    {
        Normalize = s.Normalize.ToString(),
        PeakTargetDb = s.PeakTargetDb,
        LoudnessTargetLufs = s.LoudnessTargetLufs,
        TruePeakDb = s.TruePeakDb,
        LoudnessRangeLu = s.LoudnessRangeLu,
        Compressor = s.Compressor.ToString(),
        HighPass = s.HighPass,
        Limiter = s.Limiter,
        LimiterCeilingDb = s.LimiterCeilingDb,
        AudioBitrateKbps = s.AudioBitrateKbps,
    };

    private static string Trunc(string s)
        => string.IsNullOrEmpty(s) ? "unknown error" : (s.Length > 300 ? s.Substring(0, 300) + "…" : s);

    private void SavePositionState(bool withPositionMs)
    {
        if (_library == null || _shuffle == null) return;
        // The playhead currently belongs to a temp preview clip, not to the track.
        if (_audioPreviewActive) return;
        try
        {
            // Always the SHUFFLE position — that is the thing we promise to
            // remember. During an excursion the playhead we want to keep is the
            // one we marked when leaving, not whatever is currently playing.
            long positionMs = _context == PlayContext.Shuffle
                ? (withPositionMs && _playback.IsPlaying ? _playback.TimeMs : 0)
                : _shuffleReturnMs;
            var pos = new PositionFile
            {
                CurrentIndex = _currentIndex,
                CurrentFileRelative = (_currentIndex >= 0 && _currentIndex < _shuffle.Files.Count)
                    ? _shuffle.Files[_currentIndex] : null,
                PositionMs = positionMs
            };
            PlaylistState.SavePosition(_library.RootFolder, pos);
        }
        catch (Exception ex)
        {
            _errorPanel.Log("Position save failed: " + ex.Message);
        }
    }

    private void RefreshSidebar()
    {
        _sidebar.SetFavoritesCount(_favorites.Count);
        if (_library == null || _shuffle == null)
        {
            _sidebar.SetItems(Array.Empty<(string, string, string)>(), null);
            return;
        }
        IEnumerable<(string, string, string)> entries;
        switch (_sidebar.Mode)
        {
            case Sidebar.ViewMode.ShuffleOrder:
                entries = _shuffle.Files.Select((rel, i) =>
                    ((i + 1).ToString(), rel, _library.ToFull(rel)));
                break;
            case Sidebar.ViewMode.Favorites:
                entries = _favorites.Select((rel, i) =>
                    ((i + 1).ToString(), rel, _library.ToFull(rel)));
                break;
            default:
                entries = FilterForSearch(_sidebar.SearchText).Select((rel, i) =>
                    ((i + 1).ToString(), rel, _library.ToFull(rel)));
                break;
        }
        _sidebar.SetItems(entries, _currentFullPath);
    }

    // Every whitespace-separated term must appear somewhere in the relative
    // path, so folder names are searchable too and term order doesn't matter.
    private IEnumerable<string> FilterForSearch(string query)
    {
        if (_library == null) return Array.Empty<string>();
        var all = _library.AlphaRelative();
        if (string.IsNullOrWhiteSpace(query)) return all;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return all;
        return all.Where(rel => terms.All(t => rel.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateNowPlayingLabel()
    {
        if (_currentFullPath == null) { _transport.NowPlayingLabel.Text = ""; return; }
        string suffix = _context switch
        {
            PlayContext.Favorites => $"    (favorite {_favIndex + 1:N0} / {_favorites.Count:N0})",
            PlayContext.OneShot => "    (search)",
            _ => _shuffle != null ? $"    ({_currentIndex + 1:N0} / {_shuffle.Files.Count:N0})" : ""
        };
        _transport.NowPlayingLabel.Text = Path.GetFileName(_currentFullPath) + suffix;
    }

    private void UpdateWindowTitle()
    {
        if (_currentFullPath != null)
            Text = $"{Path.GetFileName(_currentFullPath)} — RandVideoPlayer";
        else if (_library != null)
            Text = $"RandVideoPlayer — {_library.RootFolder}";
        else
            Text = "RandVideoPlayer";
    }

    private void ResetPlayer()
    {
        // Full pipeline rebuild. The PlaybackController.Recycle call disposes
        // the current MediaPlayer and LibVLC, creates fresh ones, fires
        // PipelineRecycled (which we use to reattach the VideoView), and then
        // replays the current file at its current playhead position.
        _errorPanel.Log("Player reset: rebuilding video/audio pipeline.");
        _automaticEngineRebuildsThisSession = 0;
        _automaticPipelineRecyclesThisSession = 0;
        _automaticEngineRebuildLimitLogged = false;
        _automaticPipelineRecycleLimitLogged = false;
        if (_playback.WorkerLooksWedged(EngineWedgeThresholdMs))
            RebuildPlaybackEngine("manual player reset with a wedged worker", automatic: false, ignoreRateLimit: true);
        else
            TryRecyclePlaybackPipeline("manual player reset", automatic: false);
    }

    private bool TryRecyclePlaybackPipeline(string why, bool automatic)
    {
        if (automatic && _automaticPipelineRecyclesThisSession >= MaxAutomaticPipelineRecyclesPerSession)
        {
            if (!_automaticPipelineRecycleLimitLogged)
            {
                _automaticPipelineRecycleLimitLogged = true;
                _errorPanel.Log("Automatic pipeline rebuild paused after "
                    + MaxAutomaticPipelineRecyclesPerSession
                    + " attempts this session to prevent runaway VLC memory growth. "
                    + "Use Player > Reset Player, or restart the app, if playback is still broken. "
                    + DescribeProcessMemory());
            }
            return false;
        }

        if (automatic) _automaticPipelineRecyclesThisSession++;
        _errorPanel.Log("Queued playback pipeline rebuild (" + why + "). " + DescribeProcessMemory());
        try { _playback.Recycle(); return true; }
        catch (Exception ex) { _errorPanel.Log("Pipeline rebuild failed: " + ex.Message); return false; }
    }

    private void CheckMemoryPressure()
    {
        if (IsDisposed) return;
        var mem = CaptureProcessMemory();
        if (mem == null) return;

        long now = Environment.TickCount64;
        long pressureBytes = Math.Max(mem.Value.PrivateBytes, mem.Value.WorkingSetBytes);
        bool high = pressureBytes >= MemoryRecoveryPrivateBytes;
        bool emergency = pressureBytes >= MemoryEmergencyPrivateBytes;
        if (!high)
        {
            if (now - _lastMemoryLogTicks >= MemoryLogMinIntervalMs)
            {
                _lastMemoryLogTicks = now;
                WriteDiagnostic("memory sample: " + FormatMemory(mem.Value));
            }
            return;
        }

        if (now - _lastMemoryRecoveryTicks < MemoryRecoveryMinIntervalMs) return;
        _lastMemoryRecoveryTicks = now;
        string context = $"memory pressure at index {_currentIndex}, file \"{Path.GetFileName(_currentFullPath ?? "")}\"";
        _errorPanel.Log("High memory detected; restarting player process. " + FormatMemory(mem.Value));
        WriteDiagnostic(context + ": " + FormatMemory(mem.Value));
        RestartProcessForMemoryPressure(emergency);
    }

    private void RestartProcessForMemoryPressure(bool emergency)
    {
        if (_memoryRestartInProgress) return;
        _memoryRestartInProgress = true;

        try
        {
            if (_shuffle != null && _library != null && _currentIndex >= 0)
            {
                string skipped = _currentFullPath ?? "";
                if (_currentIndex + 1 < _shuffle.Files.Count)
                {
                    _currentIndex++;
                    _currentFullPath = _library.ToFull(_shuffle.Files[_currentIndex]);
                    SavePositionState(withPositionMs: false);
                    WriteDiagnostic("memory restart skipped suspect file: \"" + Path.GetFileName(skipped) + "\"; next index " + _currentIndex);
                }
                else
                {
                    SavePositionState(withPositionMs: false);
                    WriteDiagnostic("memory restart at end of playlist; saved current index " + _currentIndex);
                }
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic("memory restart position save failed: " + ex.Message);
        }

        try
        {
            var exe = Application.ExecutablePath;
            WriteDiagnostic("starting replacement process: " + exe + (emergency ? " (emergency)" : ""));
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WriteDiagnostic("replacement process start failed: " + ex.Message);
        }

        Environment.Exit(99);
    }

    private static string DescribeProcessMemory()
    {
        try
        {
            var mem = CaptureProcessMemory();
            return mem == null ? "" : "Memory: " + FormatMemory(mem.Value) + ".";
        }
        catch { return ""; }
    }

    private static (long WorkingSetBytes, long PrivateBytes, long VirtualBytes, int HandleCount, int ThreadCount)? CaptureProcessMemory()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return (p.WorkingSet64, p.PrivateMemorySize64, p.VirtualMemorySize64, p.HandleCount, p.Threads.Count);
        }
        catch { return null; }
    }

    private static string FormatMemory((long WorkingSetBytes, long PrivateBytes, long VirtualBytes, int HandleCount, int ThreadCount) mem)
    {
        return "working set " + FormatBytes(mem.WorkingSetBytes)
            + ", private " + FormatBytes(mem.PrivateBytes)
            + ", virtual " + FormatBytes(mem.VirtualBytes)
            + ", handles " + mem.HandleCount
            + ", threads " + mem.ThreadCount;
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLogPath)!);
            File.AppendAllText(DiagnosticLogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024d * 1024d);
        return mb >= 1024d ? (mb / 1024d).ToString("0.0") + " GB" : mb.ToString("0") + " MB";
    }

    private void ToggleSidebar() { _sidebar.Visible = !_sidebar.Visible; _settings.SidebarVisible = _sidebar.Visible; }
    private void ToggleErrorPanel() { _errorPanel.Visible = !_errorPanel.Visible; _settings.ErrorPanelVisible = _errorPanel.Visible; }

    private void ToggleDarkMode()
    {
        _settings.DarkMode = !_settings.DarkMode;
        _theme = _settings.DarkMode ? Theme.Dark : Theme.Light;
        if (_darkItem != null) _darkItem.Checked = _settings.DarkMode;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        BackColor = _theme.Background;
        _menu.BackColor = _theme.MenuBack;
        _menu.ForeColor = _theme.Text;
        _menu.Renderer = new ThemedMenuRenderer(_theme);
        ApplyMenuForeColorRecursive(_menu.Items);
        _transport.ApplyTheme(_theme);
        _sidebar.ApplyTheme(_theme);
        _errorPanel.ApplyTheme(_theme);
        _videoHost.BackColor = Color.Black;
        if (_cutWindow != null && !_cutWindow.IsDisposed) _cutWindow.ApplyTheme(_theme);
        if (_audioWindow != null && !_audioWindow.IsDisposed) _audioWindow.ApplyTheme(_theme);

        // Native chrome (title bar + scrollbars) — Windows-only dark-mode hooks.
        if (IsHandleCreated)
            DarkChrome.ApplyTitleBar(Handle, _theme.IsDark);
        DarkChrome.ApplyTreeTheme(this, _theme.IsDark);

        Invalidate(true);
    }

    private void ApplyMenuForeColorRecursive(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = _theme.Text;
            item.BackColor = _theme.MenuBack;
            if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
                ApplyMenuForeColorRecursive(mi.DropDownItems);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkChrome.ApplyTitleBar(Handle, _theme.IsDark);

        // Ask Windows to tell us when the console display turns off/on so we can
        // auto-rebuild the video pipeline after the monitors come back.
        if (_displayNotify == IntPtr.Zero)
        {
            try
            {
                var guid = Win32.GUID_CONSOLE_DISPLAY_STATE;
                _displayNotify = Win32.RegisterPowerSettingNotification(
                    Handle, ref guid, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            catch { }
        }
        _lastScreenCount = SafeScreenCount();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterDisplayNotify();
        base.OnHandleDestroyed(e);
    }

    private void UnregisterDisplayNotify()
    {
        if (_displayNotify != IntPtr.Zero)
        {
            try { Win32.UnregisterPowerSettingNotification(_displayNotify); } catch { }
            _displayNotify = IntPtr.Zero;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Win32.WM_POWERBROADCAST:
                if ((int)m.WParam == Win32.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    try
                    {
                        var s = Marshal.PtrToStructure<Win32.POWERBROADCAST_SETTING>(m.LParam);
                        if (s.PowerSetting == Win32.GUID_CONSOLE_DISPLAY_STATE)
                            OnConsoleDisplayState(s.Data); // 0 = off, 1 = on, 2 = dimmed
                    }
                    catch { }
                }
                break;
            case Win32.WM_DISPLAYCHANGE:
                OnDisplayTopologyChanged();
                break;
        }
        base.WndProc(ref m);
    }

    // The OS turned the display off (idle "turn off display after N min") and
    // later back on, all while the PC stayed awake. This is the exact case
    // PowerModes.Resume misses.
    private void OnConsoleDisplayState(byte state)
    {
        // 0 = off, 1 = on, 2 = dimmed. Treat ANYTHING that isn't a clean "on"
        // as "the display left its normal state" — some drivers report the idle
        // sequence as on -> dimmed -> on and never deliver a clean 0, but the
        // GPU can still have dropped its D3D device during the blank. Recovery
        // is debounced and reuses the existing player, so an over-trigger just
        // costs one cheap replay; a MISSED off->on costs a black screen.
        if (state != 1) _displayWasOff = true;
        else if (_displayWasOff)
        {
            _displayWasOff = false;
            ScheduleDisplayRecovery();
        }
    }

    // Resolution/topology change. A monitor dropping or restoring its link
    // (common when physically powered off, especially over DisplayPort) shows
    // up here even when the console-display-state notification doesn't fire.
    // We recover if we'd already seen an "off", or if a monitor reappeared.
    private void OnDisplayTopologyChanged()
    {
        int now = SafeScreenCount();
        bool monitorReturned = _lastScreenCount >= 0 && now > _lastScreenCount;
        _lastScreenCount = now;
        if (_displayWasOff || monitorReturned)
        {
            _displayWasOff = false;
            ScheduleDisplayRecovery();
        }
    }

    private static int SafeScreenCount()
    {
        try { return Screen.AllScreens.Length; } catch { return 1; }
    }

    // Debounce: coalesce the burst of power/display messages that arrive when
    // displays wake, and give the GPU a moment to settle before rebuilding.
    // 3s rather than something snappier: this machine's display-wake is rough
    // enough to crash other apps, so don't poke D3D mid-transition.
    private void ScheduleDisplayRecovery()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (_displayRecoveryTimer == null)
        {
            _displayRecoveryTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _displayRecoveryTimer.Tick += (_, __) =>
            {
                _displayRecoveryTimer!.Stop();
                DoDisplayRecovery();
            };
        }
        _displayRecoveryTimer.Stop();
        _displayRecoveryTimer.Start();
    }

    private void DoDisplayRecovery()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (_currentFullPath == null) return; // nothing loaded — nothing to rebuild
        if (_playback.WorkerLooksWedged(EngineWedgeThresholdMs))
        {
            // The worker starved while the display was away (a Play/Stop that
            // wedged mid-transition). A queued replay would just sit behind the
            // stuck call forever — replace the whole engine instead.
            // A display event means conditions genuinely changed, so the
            // consecutive rebuild allowance starts fresh. The session-level
            // budget remains in place to prevent overnight memory blowups.
            _consecutiveEngineRebuilds = 0;
            RebuildPlaybackEngine("the engine wedged while the display was off", automatic: true, ignoreRateLimit: false);
            return;
        }
        // Common case: libvlc is fine, only its video output died. Replay the
        // current file on the EXISTING player — it keeps its window binding, so
        // the video re-embeds in place. No new player is created, so there is
        // nothing that can escape into a detached standalone window.
        _errorPanel.Log("Display/system resumed — restarting playback on the current video.");
        try { _playback.ReplayCurrent(); }
        catch (Exception ex) { _errorPanel.Log("Auto display-recovery failed: " + ex.Message); }
        StartDetachedWindowAdoptionSweep();
    }

    private void StartDetachedWindowAdoptionSweep()
    {
        if (IsDisposed || !IsHandleCreated) return;
        AdoptDetachedVideoWindows();
        _detachedWindowAdoptionTicksRemaining = 20;
        if (_detachedWindowAdoptionTimer == null)
        {
            _detachedWindowAdoptionTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _detachedWindowAdoptionTimer.Tick += (_, __) =>
            {
                if (IsDisposed || _detachedWindowAdoptionTicksRemaining-- <= 0)
                {
                    _detachedWindowAdoptionTimer!.Stop();
                    return;
                }
                AdoptDetachedVideoWindows();
            };
        }
        _detachedWindowAdoptionTimer.Stop();
        _detachedWindowAdoptionTimer.Start();
    }

    private void AdoptDetachedVideoWindows()
    {
        if (IsDisposed || !IsHandleCreated || !_videoHost.VideoView.IsHandleCreated) return;

        var adoptedThisPass = 0;
        foreach (var hwnd in EnumerateDetachedProcessWindows())
        {
            try
            {
                var title = Win32.GetWindowTextSafe(hwnd);
                var cls = Win32.GetClassNameSafe(hwnd);

                var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
                style &= ~(Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_MINIMIZEBOX |
                           Win32.WS_MAXIMIZEBOX | Win32.WS_SYSMENU | Win32.WS_POPUP);
                style |= Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS | Win32.WS_CLIPCHILDREN;
                Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style));

                Win32.SetParent(hwnd, _videoHost.VideoView.Handle);
                _adoptedVideoWindows.Add(hwnd);
                FitAdoptedVideoWindow(hwnd);
                adoptedThisPass++;

                _errorPanel.Log("Adopted detached video window back into the player"
                    + (string.IsNullOrWhiteSpace(title) ? "" : $" (title: {title})")
                    + (string.IsNullOrWhiteSpace(cls) ? "." : $", class: {cls}."));
            }
            catch (Exception ex)
            {
                _errorPanel.Log("Failed to adopt detached video window: " + ex.Message);
            }
        }

        if (adoptedThisPass == 0)
            FitAdoptedVideoWindows();
    }

    private IEnumerable<IntPtr> EnumerateDetachedProcessWindows()
    {
        var result = new List<IntPtr>();
        var thisPid = Environment.ProcessId;
        var mainRoot = Handle;

        Win32.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == IntPtr.Zero || hwnd == mainRoot) return true;
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != thisPid) return true;
                if (!Win32.IsWindowVisible(hwnd)) return true;

                var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
                if (root == mainRoot) return true; // already part of our main window tree
                if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return true; // dialogs/tool windows
                if (!LooksLikeNativeDetachedVideoWindow(hwnd)) return true;

                result.Add(hwnd);
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static bool LooksLikeNativeDetachedVideoWindow(IntPtr hwnd)
    {
        var cls = Win32.GetClassNameSafe(hwnd);
        if (string.IsNullOrWhiteSpace(cls)) return true;

        // Avoid swallowing our own menus/dialogs/tooltips if the sweep overlaps
        // with user input. LibVLC's standalone vout window is a native window,
        // not a WinForms control or common dialog/menu class.
        if (cls.StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(cls, "#32768", StringComparison.OrdinalIgnoreCase)) return false; // menu
        if (string.Equals(cls, "#32770", StringComparison.OrdinalIgnoreCase)) return false; // dialog
        if (string.Equals(cls, "tooltips_class32", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private void FitAdoptedVideoWindows()
    {
        if (IsDisposed || !_videoHost.VideoView.IsHandleCreated) return;
        foreach (var hwnd in _adoptedVideoWindows.ToList())
        {
            if (!Win32.IsWindow(hwnd))
            {
                _adoptedVideoWindows.Remove(hwnd);
                continue;
            }
            FitAdoptedVideoWindow(hwnd);
        }
    }

    private void FitAdoptedVideoWindow(IntPtr hwnd)
    {
        var size = _videoHost.VideoView.ClientSize;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
            Math.Max(1, size.Width), Math.Max(1, size.Height),
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
    }

    private void RebuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        if (_settings.Recent.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
            return;
        }
        foreach (var p in _settings.Recent.ToList())
        {
            var item = new ToolStripMenuItem(p);
            var captured = p;
            item.Click += (_, __) =>
            {
                if (Directory.Exists(captured)) OpenFolder(captured);
                else
                {
                    _settings.Recent.Remove(captured);
                    if (string.Equals(_settings.LastFolder, captured, StringComparison.OrdinalIgnoreCase))
                        _settings.LastFolder = null;
                    _settings.Save();
                    RebuildRecentMenu();
                }
            };
            _recentMenu.DropDownItems.Add(item);
        }
    }

    private void StartWatcher(string folder)
    {
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
        };
        _watcher.Created += (_, e) => QueueChange(e.FullPath, add: true);
        _watcher.Deleted += (_, e) => QueueChange(e.FullPath, add: false);
        _watcher.Renamed += (_, e) =>
        {
            QueueChange(e.OldFullPath, add: false);
            QueueChange(e.FullPath, add: true);
        };
        _watcher.EnableRaisingEvents = true;

        _watcherDebounce = new System.Windows.Forms.Timer { Interval = 600 };
        _watcherDebounce.Tick += (_, __) => { _watcherDebounce!.Stop(); FlushPendingChanges(); };
    }

    private void StopWatcher()
    {
        try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
        _watcher = null;
        try { _watcherDebounce?.Stop(); _watcherDebounce?.Dispose(); } catch { }
        _watcherDebounce = null;
        _pendingAdds.Clear();
        _pendingRemoves.Clear();
    }

    private void QueueChange(string fullPath, bool add)
    {
        var ext = Path.GetExtension(fullPath);
        if (!FolderLibrary.PlayableExtensions.Contains(ext)) return;
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(new Action(() => QueueChange(fullPath, add))); return; }
        if (add) { _pendingAdds.Add(fullPath); _pendingRemoves.Remove(fullPath); }
        else { _pendingRemoves.Add(fullPath); _pendingAdds.Remove(fullPath); }
        _watcherDebounce?.Stop();
        _watcherDebounce?.Start();
    }

    private void FlushPendingChanges()
    {
        if (_library == null || _shuffle == null) return;
        var adds = _pendingAdds.ToList(); _pendingAdds.Clear();
        var rems = _pendingRemoves.ToList(); _pendingRemoves.Clear();

        _library.Rescan();

        foreach (var full in rems)
        {
            if (File.Exists(full)) continue;
            var rel = _library.ToRelative(full);
            RemoveFromShuffle(rel, logWhy: null);
            RemoveFavoriteRel(rel);
        }
        foreach (var full in adds)
        {
            if (!File.Exists(full)) continue;
            var rel = _library.ToRelative(full);
            if (_shuffle.Files.Any(s => string.Equals(s, rel, StringComparison.OrdinalIgnoreCase))) continue;
            int afterIdx = Math.Max(_currentIndex, -1);
            int insertAt = ShuffleEngine.PickInsertionIndex(_shuffle.Seed, rel, afterIdx, _shuffle.Files.Count);
            _shuffle.Files.Insert(insertAt, rel);
            if (insertAt <= _currentIndex) _currentIndex++;
        }
        PlaylistState.SaveShuffle(_library.RootFolder, _shuffle);
        RefreshSidebar();
        UpdateNowPlayingLabel();
        // Rekick the duration scan to pick up new files (cached entries won't be re-probed).
        _durations?.StartOrUpdate(_library.AlphaList);
    }

    private static Sidebar.ViewMode ParseSidebarTab(string? tab) => tab switch
    {
        "search" => Sidebar.ViewMode.Search,
        "favorites" => Sidebar.ViewMode.Favorites,
        _ => Sidebar.ViewMode.ShuffleOrder
    };

    // The sidebar search box needs Space/arrows/M to reach it as text, so the
    // global transport shortcuts stand down while a text field has focus.
    private bool FocusIsInTextEntry()
    {
        Control? c = this;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c is TextBoxBase;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (FocusIsInTextEntry()) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Space: _playback.TogglePause(); return true;
            case Keys.Right: _playback.TimeMs = Math.Min(_playback.LengthMs, _playback.TimeMs + 5000); return true;
            case Keys.Left: _playback.TimeMs = Math.Max(0, _playback.TimeMs - 5000); return true;
            case Keys.Up: _transport.Volume.Value += 5; return true;
            case Keys.Down: _transport.Volume.Value -= 5; return true;
            case Keys.M:
                _playback.Muted = !_playback.Muted;
                _transport.SetMuteGlyph(_playback.Muted);
                _settings.Muted = _playback.Muted;
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        try
        {
            SavePositionState(withPositionMs: true);
            if (WindowState == FormWindowState.Normal)
                _settings.WindowBounds = new WindowBounds { X = Left, Y = Top, W = Width, H = Height, Maximized = false };
            else
            {
                var rb = RestoreBounds;
                _settings.WindowBounds = new WindowBounds { X = rb.X, Y = rb.Y, W = rb.Width, H = rb.Height, Maximized = WindowState == FormWindowState.Maximized };
            }
            _settings.SidebarTab = _sidebar.Mode switch
            {
                Sidebar.ViewMode.Search => "search",
                Sidebar.ViewMode.Favorites => "favorites",
                _ => "shuffle"
            };
            _settings.Save();
        }
        catch { }
        try { if (_audioPreviewFile != null) DeleteTempLater(_audioPreviewFile); } catch { }
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { UnregisterDisplayNotify(); } catch { }
        try { _uiTimer.Stop(); _uiTimer.Dispose(); } catch { }
        try { _positionSaveTimer.Stop(); _positionSaveTimer.Dispose(); } catch { }
        try { _memoryWatchdog.Stop(); _memoryWatchdog.Dispose(); } catch { }
        try { StopWatcher(); } catch { }
        try { _displayRecoveryTimer?.Stop(); _displayRecoveryTimer?.Dispose(); } catch { }
        try { _detachedWindowAdoptionTimer?.Stop(); _detachedWindowAdoptionTimer?.Dispose(); } catch { }
        try { _engineWatchdog?.Stop(); _engineWatchdog?.Dispose(); } catch { }
        try { _mouseHook.Dispose(); } catch { }
        try { DisposeDurationIndex(); } catch { }
        try { _playback.Dispose(); } catch { }
    }
}


internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly Theme _t;
    public ThemedMenuRenderer(Theme t) : base(new ThemedColors(t)) { _t = t; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _t.Text : _t.TextMuted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _t.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Draw a visible check mark in theme colors (the default glyph can be invisible in dark mode).
        using var pen = new System.Drawing.Pen(_t.Text, 2f);
        var r = e.ImageRectangle;
        int cx = r.Left + 2, cy = r.Top + r.Height / 2;
        e.Graphics.DrawLine(pen, cx, cy, cx + 3, cy + 3);
        e.Graphics.DrawLine(pen, cx + 3, cy + 3, r.Right - 2, r.Top + 2);
    }
}

internal sealed class ThemedColors : ProfessionalColorTable
{
    private readonly Theme _t;
    public ThemedColors(Theme t) { _t = t; UseSystemColors = false; }
    public override Color MenuItemSelected => _t.ButtonHover;
    public override Color MenuItemSelectedGradientBegin => _t.ButtonHover;
    public override Color MenuItemSelectedGradientEnd => _t.ButtonHover;
    public override Color MenuItemPressedGradientBegin => _t.ButtonActive;
    public override Color MenuItemPressedGradientEnd => _t.ButtonActive;
    public override Color MenuItemBorder => _t.Border;
    public override Color MenuBorder => _t.Border;
    public override Color ToolStripDropDownBackground => _t.MenuBack;
    public override Color ImageMarginGradientBegin => _t.MenuBack;
    public override Color ImageMarginGradientMiddle => _t.MenuBack;
    public override Color ImageMarginGradientEnd => _t.MenuBack;
    public override Color ToolStripBorder => _t.Border;
    public override Color MenuStripGradientBegin => _t.MenuBack;
    public override Color MenuStripGradientEnd => _t.MenuBack;
    public override Color SeparatorDark => _t.Border;
    public override Color SeparatorLight => _t.Border;
}

internal static class SystemSounds
{
    public static void Beep() { try { System.Media.SystemSounds.Beep.Play(); } catch { } }
}

internal static class Win32
{
    public const uint GA_ROOT = 2;
    public const uint GW_OWNER = 4;

    // Display power-state notifications.
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    public const int GWL_STYLE = -16;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_POPUP = 0x80000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_CLIPSIBLINGS = 0x04000000L;
    public const long WS_CLIPCHILDREN = 0x02000000L;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    // GUID_CONSOLE_DISPLAY_STATE — fires when the console display turns
    // off (0), on (1), or dims (2), even though the system never sleeps.
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
        new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // first byte of the payload; for display-state it's 0/1/2
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int count);

    public static string GetWindowTextSafe(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    public static string GetClassNameSafe(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
