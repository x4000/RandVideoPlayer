using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RandVideoPlayer.Integrations;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

public sealed class Sidebar : UserControl, IThemedControl
{
    public enum ViewMode { Search, ShuffleOrder, Favorites }

    public ListView List { get; }
    public Button SearchBtn { get; }
    public Button ShuffleBtn { get; }
    public FavoritesButton FavoritesBtn { get; }
    public Label StatsLabel { get; }
    public TextBox SearchBox { get; }

    public event Action<string>? PlayRequested;
    public event Action<string>? RevealRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string>? CutRequested;
    public event Action<ViewMode>? ViewModeChanged;
    public event Action? SearchTextChanged;
    public event Action<string>? AddFavoriteRequested;
    public event Action<string>? RemoveFavoriteRequested;
    // (dragged file, index it should occupy in the pre-move list)
    public event Action<string, int>? FavoriteMoveRequested;

    // Set by the host so the context menu can offer add-vs-remove correctly.
    public Func<string, bool>? IsFavorite { get; set; }

    private Theme _theme = Theme.Dark;
    private ViewMode _mode = ViewMode.ShuffleOrder;
    private string? _currentFullPath;
    private readonly Panel _tabs;
    private readonly Panel _searchPanel;
    private readonly System.Windows.Forms.Timer _searchDebounce;
    // Row index the pending drag would insert before; == Items.Count means "at
    // the end". -1 when no drag is in flight.
    private int _dropIndex = -1;

    public ViewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            _searchPanel.Visible = _mode == ViewMode.Search;
            _dropIndex = -1;
            UpdateToggleAppearance();
            ViewModeChanged?.Invoke(_mode);
            if (_mode == ViewMode.Search && IsHandleCreated)
            {
                try { SearchBox.Focus(); SearchBox.SelectAll(); } catch { }
            }
        }
    }

    public string SearchText => SearchBox.Text.Trim();

    public Sidebar()
    {
        Width = 320;
        Dock = DockStyle.Right;

        // Tabs header. Widths are split evenly on resize rather than fixed, so
        // three labels still fit when the sidebar is narrow.
        _tabs = new Panel { Dock = DockStyle.Top, Height = 34 };
        SearchBtn = MakeTabButton("Search");
        ShuffleBtn = MakeTabButton("Shuffled");
        FavoritesBtn = MakeFavoritesTabButton();
        SearchBtn.Click += (_, __) => Mode = ViewMode.Search;
        ShuffleBtn.Click += (_, __) => Mode = ViewMode.ShuffleOrder;
        FavoritesBtn.Click += (_, __) => Mode = ViewMode.Favorites;
        // Add right-to-left: each Dock=Left control stacks after the previous.
        _tabs.Controls.Add(FavoritesBtn);
        _tabs.Controls.Add(ShuffleBtn);
        _tabs.Controls.Add(SearchBtn);
        _tabs.Resize += (_, __) => LayoutTabs();

        // Stats row
        var statsPanel = new Panel { Dock = DockStyle.Top, Height = 22 };
        StatsLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            Font = new Font("Segoe UI", 8.5f),
            Text = ""
        };
        statsPanel.Controls.Add(StatsLabel);

        // Search row — only shown on the Search tab.
        _searchPanel = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(6, 2, 6, 4), Visible = false };
        SearchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f),
            PlaceholderText = "Filter by name…"
        };
        _searchDebounce = new System.Windows.Forms.Timer { Interval = 180 };
        _searchDebounce.Tick += (_, __) => { _searchDebounce.Stop(); SearchTextChanged?.Invoke(); };
        SearchBox.TextChanged += (_, __) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchPanel.Controls.Add(SearchBox);

        // List
        List = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.None,
            Font = new Font("Segoe UI", 9f),
            BorderStyle = BorderStyle.None,
            OwnerDraw = true,
            ShowItemToolTips = true,
            AllowDrop = true
        };
        List.Columns.Add("#", 58);
        List.Columns.Add("File", 240);
        List.Resize += (_, __) => ResizeColumns();
        List.DoubleClick += (_, __) =>
        {
            if (List.SelectedItems.Count > 0 && List.SelectedItems[0].Tag is string path)
                PlayRequested?.Invoke(path);
        };
        List.DrawColumnHeader += (s, e) => e.DrawDefault = true;
        List.DrawSubItem += DrawSubItem;
        List.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter && List.SelectedItems.Count > 0
                && List.SelectedItems[0].Tag is string path)
            {
                PlayRequested?.Invoke(path);
                e.Handled = true;
            }
        };

        // Wired here rather than with the rest of the search box because it
        // reaches into the list, which does not exist until now.
        SearchBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)
            {
                // Commit any pending debounce so the list we jump into is current.
                if (_searchDebounce.Enabled) { _searchDebounce.Stop(); SearchTextChanged?.Invoke(); }
                if (List.Items.Count > 0)
                {
                    List.Focus();
                    List.Items[0].Selected = true;
                    try { List.Items[0].EnsureVisible(); } catch { }
                }
                e.Handled = e.SuppressKeyPress = true;
            }
        };

        // Dragging a row does two things depending on where it lands: inside the
        // favorites list it reorders (only that tab is an ordered, editable
        // list); onto the Favorites tab button, from any tab, it favorites.
        List.ItemDrag += (s, e) =>
        {
            if (e.Item is ListViewItem lvi && lvi.Tag is string p)
                List.DoDragDrop(p, DragDropEffects.Move | DragDropEffects.Copy);
        };
        List.DragEnter += (s, e) => e.Effect = DragEffectFor(e);
        List.DragOver += (s, e) =>
        {
            e.Effect = DragEffectFor(e);
            int idx = e.Effect == DragDropEffects.Move ? DropIndexAt(e.X, e.Y) : -1;
            if (idx != _dropIndex) { _dropIndex = idx; List.Invalidate(); }
        };
        List.DragLeave += (s, e) => { if (_dropIndex != -1) { _dropIndex = -1; List.Invalidate(); } };
        List.DragDrop += (s, e) =>
        {
            int target = _dropIndex;
            _dropIndex = -1;
            List.Invalidate();
            if (_mode != ViewMode.Favorites || target < 0) return;
            if (e.Data?.GetData(typeof(string)) is string p) FavoriteMoveRequested?.Invoke(p, target);
        };

        var menu = new ContextMenuStrip();
        var miPlay = new ToolStripMenuItem("Play");
        var miReveal = new ToolStripMenuItem("Reveal in Explorer");
        var miDelete = new ToolStripMenuItem("Delete (Recycle Bin)");
        var miAddFav = new ToolStripMenuItem("Add to Favorites");
        var miRemoveFav = new ToolStripMenuItem("Remove from Favorites");
        var miCut = new ToolStripMenuItem("Cut… (lossless)");
        menu.Items.AddRange(new ToolStripItem[]
        {
            miPlay, miReveal, miDelete,
            new ToolStripSeparator(), miAddFav, miRemoveFav,
            new ToolStripSeparator(), miCut
        });
        menu.Opening += (s, e) =>
        {
            var path = SelectedPath();
            bool has = path != null;
            miPlay.Enabled = miReveal.Enabled = miDelete.Enabled = has;
            bool isFav = has && (IsFavorite?.Invoke(path!) ?? false);
            miAddFav.Visible = has && !isFav;
            miRemoveFav.Visible = has && isFav;
            miCut.Enabled = has && Ffmpeg.IsAvailable;
            miCut.Text = Ffmpeg.IsAvailable ? "Cut… (lossless)" : "Cut… (ffmpeg not found)";
            if (!has) e.Cancel = true;
        };
        string? SelectedPath() => List.SelectedItems.Count > 0 && List.SelectedItems[0].Tag is string s ? s : null;
        miPlay.Click += (_, __) => { var p = SelectedPath(); if (p != null) PlayRequested?.Invoke(p); };
        miReveal.Click += (_, __) => { var p = SelectedPath(); if (p != null) RevealRequested?.Invoke(p); };
        miDelete.Click += (_, __) => { var p = SelectedPath(); if (p != null) DeleteRequested?.Invoke(p); };
        miAddFav.Click += (_, __) => { var p = SelectedPath(); if (p != null) AddFavoriteRequested?.Invoke(p); };
        miRemoveFav.Click += (_, __) => { var p = SelectedPath(); if (p != null) RemoveFavoriteRequested?.Invoke(p); };
        miCut.Click += (_, __) => { var p = SelectedPath(); if (p != null) CutRequested?.Invoke(p); };
        List.ContextMenuStrip = menu;

        Controls.Add(List);
        Controls.Add(_searchPanel);
        Controls.Add(statsPanel);
        Controls.Add(_tabs);

        ApplyTheme(_theme);
        UpdateToggleAppearance();
        LayoutTabs();
    }

    private Button MakeTabButton(string text) => new()
    {
        Text = text,
        Width = 106,
        Dock = DockStyle.Left,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9f),
        TabStop = false
    };

    private FavoritesButton MakeFavoritesTabButton() => new()
    {
        Text = "Favorites",
        Width = 106,
        Dock = DockStyle.Left,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9f),
        TabStop = false
    };

    private void LayoutTabs()
    {
        int total = _tabs.ClientSize.Width;
        if (total <= 0) return;
        int each = total / 3;
        SearchBtn.Width = each;
        ShuffleBtn.Width = each;
        // Last one absorbs the rounding remainder.
        FavoritesBtn.Width = Math.Max(each, total - 2 * each);
    }

    private DragDropEffects DragEffectFor(DragEventArgs e)
    {
        if (_mode != ViewMode.Favorites) return DragDropEffects.None;
        return e.Data?.GetDataPresent(typeof(string)) == true ? DragDropEffects.Move : DragDropEffects.None;
    }

    // Screen coords -> the slot the row would land in. Above the midpoint of a
    // row means "before it", below means "after it".
    private int DropIndexAt(int screenX, int screenY)
    {
        var pt = List.PointToClient(new Point(screenX, screenY));
        if (List.Items.Count == 0) return 0;
        var hit = List.GetItemAt(4, pt.Y);
        if (hit == null)
        {
            // Above the first row, or in the empty space past the last one.
            return pt.Y < List.Items[0].Bounds.Top ? 0 : List.Items.Count;
        }
        var b = hit.Bounds;
        return pt.Y < b.Top + b.Height / 2 ? hit.Index : hit.Index + 1;
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null) { e.DrawDefault = true; return; }
        bool isCurrent = e.Item.Tag is string s && _currentFullPath != null
                         && string.Equals(s, _currentFullPath, StringComparison.OrdinalIgnoreCase);

        Color back = e.Item.Selected ? _theme.ListSelection
                   : isCurrent ? _theme.CurrentTrack
                   : (e.ItemIndex % 2 == 0 ? _theme.ListRowEven : _theme.ListRowOdd);
        Color fore = _theme.Text;

        using (var bg = new SolidBrush(back)) e.Graphics.FillRectangle(bg, e.Bounds);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        // Index column: right-aligned. File column: left-aligned with small left padding.
        var textRect = e.Bounds;
        if (e.ColumnIndex == 0)
        {
            flags |= TextFormatFlags.Right;
            textRect.Width -= 6;
        }
        else
        {
            flags |= TextFormatFlags.Left;
            textRect.X += 4;
            textRect.Width -= 8;
        }
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, List.Font, textRect, fore, flags);

        // Drop indicator for a favorites reorder. ListView.InsertionMark only
        // renders in icon views, so draw the line ourselves.
        if (_dropIndex >= 0)
        {
            bool lineAbove = e.ItemIndex == _dropIndex;
            bool lineBelow = _dropIndex >= List.Items.Count && e.ItemIndex == List.Items.Count - 1;
            if (lineAbove || lineBelow)
            {
                int y = lineAbove ? e.Bounds.Top : e.Bounds.Bottom - 2;
                using var pen = new Pen(_theme.Accent, 2);
                e.Graphics.DrawLine(pen, e.Bounds.Left, y + 1, e.Bounds.Right, y + 1);
            }
        }
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.PanelAlt;
        foreach (Control c in Controls)
        {
            if (c is Panel p) p.BackColor = theme.PanelAlt;
        }
        StatsLabel.ForeColor = theme.TextMuted;
        StatsLabel.BackColor = theme.PanelAlt;
        foreach (var b in TabButtons())
        {
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = theme.PanelAlt;
            b.ForeColor = theme.Text;
        }
        SearchBox.BackColor = theme.Background;
        SearchBox.ForeColor = theme.Text;
        List.BackColor = theme.Background;
        List.ForeColor = theme.Text;
        UpdateToggleAppearance();
        ResizeColumns();
        Invalidate(true);
    }

    private IEnumerable<Button> TabButtons()
    {
        yield return SearchBtn;
        yield return ShuffleBtn;
        yield return FavoritesBtn;
    }

    private void UpdateToggleAppearance()
    {
        SearchBtn.BackColor = _mode == ViewMode.Search ? _theme.ButtonActive : _theme.PanelAlt;
        ShuffleBtn.BackColor = _mode == ViewMode.ShuffleOrder ? _theme.ButtonActive : _theme.PanelAlt;
        FavoritesBtn.BackColor = _mode == ViewMode.Favorites ? _theme.ButtonActive : _theme.PanelAlt;
        foreach (var b in TabButtons()) b.ForeColor = _theme.Text;
    }

    // Size the file column so the two columns together exactly fill the client
    // width (minus the vertical scrollbar, if present). Prevents horizontal scroll.
    private void ResizeColumns()
    {
        if (!IsHandleCreated) return;
        int vScrollW = VerticalScrollBarWidth();
        int available = Math.Max(80, List.ClientSize.Width - vScrollW);
        int numW = 58;
        int fileW = Math.Max(80, available - numW);
        if (List.Columns.Count >= 2)
        {
            List.Columns[0].Width = numW;
            List.Columns[1].Width = fileW;
        }
    }

    private int VerticalScrollBarWidth()
    {
        // Only present when content overflows; assume present when items > ~visible.
        int rowHeight = 18;
        int visible = Math.Max(1, List.ClientSize.Height / rowHeight);
        return (List.Items.Count > visible) ? SystemInformation.VerticalScrollBarWidth : 0;
    }

    // entries: (number, text, fullPath)
    public void SetItems(IEnumerable<(string number, string text, string fullPath)> entries,
                         string? currentFullPath)
    {
        _currentFullPath = currentFullPath;
        List.BeginUpdate();
        List.Items.Clear();
        foreach (var e in entries)
        {
            var lvi = new ListViewItem(e.number);
            lvi.SubItems.Add(e.text);
            lvi.Tag = e.fullPath;
            lvi.ToolTipText = e.fullPath;
            List.Items.Add(lvi);
        }
        List.EndUpdate();
        ResizeColumns();
        // Chasing the playing file makes sense for the ordered lists; in search
        // the user is looking at their own query results, so leave the scroll be.
        if (_mode != ViewMode.Search) EnsureCurrentVisible();
    }

    public void HighlightPath(string? fullPath)
    {
        _currentFullPath = fullPath;
        List.Invalidate();
        if (_mode != ViewMode.Search) EnsureCurrentVisible();
    }

    public void EnsureCurrentVisible()
    {
        if (_currentFullPath == null) return;
        for (int i = 0; i < List.Items.Count; i++)
        {
            if (List.Items[i].Tag is string s
                && string.Equals(s, _currentFullPath, StringComparison.OrdinalIgnoreCase))
            {
                try { List.Items[i].EnsureVisible(); } catch { }
                break;
            }
        }
    }

    // Shown on the tab itself so favoriting from another tab (via the context
    // menu, or by dropping a row onto this button) is visibly acknowledged.
    public void SetFavoritesCount(int count)
    {
        FavoritesBtn.Text = count > 0 ? $"Favorites ({count})" : "Favorites";
    }

    public void SetStats(int count, long totalMs, bool scanning, int scanned)
    {
        string durText;
        if (totalMs <= 0) durText = scanning ? "computing…" : "0s";
        else
        {
            var ts = TimeSpan.FromMilliseconds(totalMs);
            if (ts.TotalDays >= 1) durText = $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            else if (ts.TotalHours >= 1) durText = $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            else durText = $"{ts.Minutes}m {ts.Seconds}s";
        }
        string scanText = scanning ? $"  (scanning {scanned}/{count})" : "";
        StatsLabel.Text = $"{count:N0} files · {durText}{scanText}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _searchDebounce.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}

// A tab button that also accepts favorites dropped onto it, so a file can be
// favorited by dragging it over from the search or shuffle list.
public sealed class FavoritesButton : Button
{
    public event Action<string>? FileDropped;

    public FavoritesButton()
    {
        AllowDrop = true;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(typeof(string)) == true ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragEnter(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(typeof(string)) == true ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragOver(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(string)) is string p) FileDropped?.Invoke(p);
        base.OnDragDrop(e);
    }
}
