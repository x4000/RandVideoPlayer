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
    public enum ViewMode { Alphabetical, ShuffleOrder }

    public ListView List { get; }
    public Button AlphaBtn { get; }
    public Button ShuffleBtn { get; }
    public Label StatsLabel { get; }

    public event Action<string>? PlayRequested;
    public event Action<string>? RevealRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string>? BandicutRequested;
    public event Action<ViewMode>? ViewModeChanged;

    private Theme _theme = Theme.Dark;
    private ViewMode _mode = ViewMode.ShuffleOrder;
    private string? _currentFullPath;

    public ViewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            UpdateToggleAppearance();
            ViewModeChanged?.Invoke(_mode);
        }
    }

    public Sidebar()
    {
        Width = 320;
        Dock = DockStyle.Right;

        // Tabs header
        var tabs = new Panel { Dock = DockStyle.Top, Height = 34 };
        AlphaBtn = MakeTabButton("Alphabetical");
        ShuffleBtn = MakeTabButton("Shuffle Order");
        AlphaBtn.Dock = DockStyle.Left;
        ShuffleBtn.Dock = DockStyle.Left;
        AlphaBtn.Click += (_, __) => Mode = ViewMode.Alphabetical;
        ShuffleBtn.Click += (_, __) => Mode = ViewMode.ShuffleOrder;
        tabs.Controls.Add(ShuffleBtn);
        tabs.Controls.Add(AlphaBtn);

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
            ShowItemToolTips = true
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

        var menu = new ContextMenuStrip();
        var miPlay = new ToolStripMenuItem("Play");
        var miReveal = new ToolStripMenuItem("Reveal in Explorer");
        var miDelete = new ToolStripMenuItem("Delete (Recycle Bin)");
        var miBandicut = new ToolStripMenuItem("Open in Bandicut");
        menu.Items.AddRange(new ToolStripItem[] { miPlay, miReveal, miDelete, new ToolStripSeparator(), miBandicut });
        menu.Opening += (s, e) =>
        {
            bool has = List.SelectedItems.Count > 0 && List.SelectedItems[0].Tag is string;
            miPlay.Enabled = miReveal.Enabled = miDelete.Enabled = has;
            miBandicut.Enabled = has && Bandicut.IsInstalled;
            miBandicut.Text = Bandicut.IsInstalled ? "Open in Bandicut" : "Open in Bandicut (not installed)";
            if (!has) e.Cancel = true;
        };
        string? SelectedPath() => List.SelectedItems.Count > 0 && List.SelectedItems[0].Tag is string s ? s : null;
        miPlay.Click += (_, __) => { var p = SelectedPath(); if (p != null) PlayRequested?.Invoke(p); };
        miReveal.Click += (_, __) => { var p = SelectedPath(); if (p != null) RevealRequested?.Invoke(p); };
        miDelete.Click += (_, __) => { var p = SelectedPath(); if (p != null) DeleteRequested?.Invoke(p); };
        miBandicut.Click += (_, __) => { var p = SelectedPath(); if (p != null) BandicutRequested?.Invoke(p); };
        List.ContextMenuStrip = menu;

        Controls.Add(List);
        Controls.Add(statsPanel);
        Controls.Add(tabs);

        ApplyTheme(_theme);
        UpdateToggleAppearance();
    }

    private Button MakeTabButton(string text) => new Button
    {
        Text = text,
        Width = 130,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9f),
        TabStop = false
    };

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
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.PanelAlt;
        foreach (Control c in Controls)
        {
            if (c is Panel p) p.BackColor = theme.PanelAlt;
        }
        AlphaBtn.ForeColor = theme.Text;
        ShuffleBtn.ForeColor = theme.Text;
        StatsLabel.ForeColor = theme.TextMuted;
        StatsLabel.BackColor = theme.PanelAlt;
        foreach (var b in new[] { AlphaBtn, ShuffleBtn })
        {
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = theme.PanelAlt;
        }
        List.BackColor = theme.Background;
        List.ForeColor = theme.Text;
        UpdateToggleAppearance();
        ResizeColumns();
        Invalidate(true);
    }

    private void UpdateToggleAppearance()
    {
        AlphaBtn.BackColor = _mode == ViewMode.Alphabetical ? _theme.ButtonActive : _theme.PanelAlt;
        ShuffleBtn.BackColor = _mode == ViewMode.ShuffleOrder ? _theme.ButtonActive : _theme.PanelAlt;
        AlphaBtn.ForeColor = _theme.Text;
        ShuffleBtn.ForeColor = _theme.Text;
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
        EnsureCurrentVisible();
    }

    public void HighlightPath(string? fullPath)
    {
        _currentFullPath = fullPath;
        List.Invalidate();
        EnsureCurrentVisible();
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
}
