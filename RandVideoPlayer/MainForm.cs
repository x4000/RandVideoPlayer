using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
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
    private readonly PlaybackController _playback;

    private readonly MenuStrip _menu;
    private readonly ToolStripMenuItem _recentMenu;
    private ToolStripMenuItem _darkItem = null!;
    private readonly VideoHost _videoHost;
    private readonly Sidebar _sidebar;
    private readonly ErrorPanel _errorPanel;
    private readonly TransportBar _transport;

    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly System.Windows.Forms.Timer _positionSaveTimer;

    private FolderLibrary? _library;
    private ShuffleFile? _shuffle;
    private DurationIndex? _durations;
    private int _currentIndex = -1;
    private string? _currentFullPath;
    private long _resumePositionMs = 0;
    private bool _resumeApplied = true;
    private FileSystemWatcher? _watcher;
    private System.Windows.Forms.Timer? _watcherDebounce;
    private readonly HashSet<string> _pendingAdds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRemoves = new(StringComparer.OrdinalIgnoreCase);

    private Theme _theme = Theme.Dark;
    private readonly ThreadMouseHook _mouseHook = new();

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _theme = _settings.DarkMode ? Theme.Dark : Theme.Light;
        Text = "RandVideoPlayer";
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.Manual;
        ApplyInitialBounds();
        KeyPreview = true;

        TryLoadAppIcon();

        _playback = new PlaybackController();
        _playback.MediaEnded += OnMediaEnded;
        _playback.MediaFailed += OnMediaFailed;
        _playback.StateChanged += OnPlaybackStateChanged;

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
        _menu.Items.AddRange(new ToolStripItem[] { fileMenu, viewMenu, plMenu });

        _videoHost = new VideoHost();
        _playback.AttachTo(_videoHost.VideoView);

        _sidebar = new Sidebar { Visible = _settings.SidebarVisible };
        _sidebar.Mode = _settings.SidebarShowShuffleOrder ? Sidebar.ViewMode.ShuffleOrder : Sidebar.ViewMode.Alphabetical;
        _sidebar.PlayRequested += path => PlayPath(path, saveState: true);
        _sidebar.RevealRequested += ShellOps.RevealInExplorer;
        _sidebar.DeleteRequested += HandleDeleteFromSidebar;
        _sidebar.BandicutRequested += Bandicut.Open;
        _sidebar.ViewModeChanged += _ => { RefreshSidebar(); _sidebar.EnsureCurrentVisible(); };

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
        _transport.PrevBtn.Click += (_, __) => GoPrev();
        _transport.NextBtn.Click += (_, __) => GoNext();
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
            var stream = asm.GetManifestResourceStream("RandVideoPlayer.app.ico");
            if (stream != null)
            {
                Icon = new Icon(stream);
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
            _durations?.Dispose();
            _durations = null;

            _library = new FolderLibrary(folder);
            _library.Rescan();
            _settings.MarkFolderUsed(folder);
            _settings.Save();
            RebuildRecentMenu();

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

            if (_currentFullPath != null && File.Exists(_currentFullPath))
                PlayPath(_currentFullPath, saveState: false);
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
        bool wasCurrent = idx == _currentIndex;
        _shuffle.Files.RemoveAt(idx);
        if (idx < _currentIndex) _currentIndex--;
        else if (idx == _currentIndex)
        {
            _currentIndex = Math.Min(_currentIndex, _shuffle.Files.Count - 1);
            _currentFullPath = _currentIndex >= 0 ? _library.ToFull(_shuffle.Files[_currentIndex]) : null;
        }
        if (logWhy != null) _errorPanel.Log(logWhy);
        if (wasCurrent && _currentIndex >= 0 && _currentFullPath != null)
            PlayPath(_currentFullPath, saveState: true);
    }

    private void PlayPath(string fullPath, bool saveState)
    {
        if (_library == null || _shuffle == null) return;
        var rel = _library.ToRelative(fullPath);
        int idx = _shuffle.Files.FindIndex(s => string.Equals(s, rel, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _currentIndex = idx;
        else _errorPanel.Log("Played ad-hoc file not in shuffle: " + rel);
        _currentFullPath = fullPath;
        _resumeApplied = true;
        _playback.Play(fullPath);
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
        if (saveState) SavePositionState(withPositionMs: false);
        UpdateNowPlayingLabel();
        UpdateWindowTitle();
        _sidebar.HighlightPath(fullPath);
    }

    private void GoPrev()
    {
        if (_shuffle == null || _library == null || _shuffle.Files.Count == 0) return;
        if (_currentIndex <= 0) { SystemSounds.Beep(); return; }
        _currentIndex--;
        _currentFullPath = _library.ToFull(_shuffle.Files[_currentIndex]);
        PlayPath(_currentFullPath, saveState: true);
    }

    private void GoNext()
    {
        if (_shuffle == null || _library == null || _shuffle.Files.Count == 0) return;
        if (_currentIndex + 1 < _shuffle.Files.Count)
        {
            _currentIndex++;
            _currentFullPath = _library.ToFull(_shuffle.Files[_currentIndex]);
            PlayPath(_currentFullPath, saveState: true);
        }
        else
        {
            ReshuffleInternal(antiRepeat: true, playFirst: true);
        }
    }

    private void OnMediaEnded()
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(GoNext));
    }

    private void OnMediaFailed(string message)
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            _errorPanel.Log(message);
            GoNext();
        }));
    }

    private void OnPlaybackStateChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
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
        _currentFullPath = _currentIndex >= 0 ? _library.ToFull(_shuffle.Files[_currentIndex]) : null;
        RefreshSidebar();
        _sidebar.EnsureCurrentVisible();
        UpdateNowPlayingLabel();
        UpdateWindowTitle();
        if (playFirst && _currentFullPath != null)
            PlayPath(_currentFullPath, saveState: true);
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
        if (isPlaying) { try { _playback.Stop(); } catch { } }

        if (!ShellOps.SendToRecycleBin(fullPath))
        {
            _errorPanel.Log("Recycle Bin delete failed: " + fullPath);
            return;
        }
    }

    private void SavePositionState(bool withPositionMs)
    {
        if (_library == null || _shuffle == null) return;
        try
        {
            var pos = new PositionFile
            {
                CurrentIndex = _currentIndex,
                CurrentFileRelative = (_currentIndex >= 0 && _currentIndex < _shuffle.Files.Count)
                    ? _shuffle.Files[_currentIndex] : null,
                PositionMs = withPositionMs && _playback.IsPlaying ? _playback.TimeMs : 0
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
        if (_library == null || _shuffle == null)
        {
            _sidebar.SetItems(Array.Empty<(string, string, string)>(), null);
            return;
        }
        IEnumerable<(string, string, string)> entries;
        if (_sidebar.Mode == Sidebar.ViewMode.ShuffleOrder)
        {
            entries = _shuffle.Files.Select((rel, i) =>
                ((i + 1).ToString("N0"), rel, _library.ToFull(rel)));
        }
        else
        {
            entries = _library.AlphaRelative().Select((rel, i) =>
                ((i + 1).ToString("N0"), rel, _library.ToFull(rel)));
        }
        _sidebar.SetItems(entries, _currentFullPath);
    }

    private void UpdateNowPlayingLabel()
    {
        if (_currentFullPath != null)
            _transport.NowPlayingLabel.Text = Path.GetFileName(_currentFullPath)
                + (_shuffle != null ? $"    ({_currentIndex + 1:N0} / {_shuffle.Files.Count:N0})" : "");
        else
            _transport.NowPlayingLabel.Text = "";
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
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
            _settings.SidebarShowShuffleOrder = _sidebar.Mode == Sidebar.ViewMode.ShuffleOrder;
            _settings.Save();
        }
        catch { }
        try { _mouseHook.Dispose(); } catch { }
        try { _durations?.Dispose(); } catch { }
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);
}
