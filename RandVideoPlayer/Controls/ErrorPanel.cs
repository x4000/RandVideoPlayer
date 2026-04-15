using System;
using System.Drawing;
using System.Windows.Forms;
using RandVideoPlayer.UI;

namespace RandVideoPlayer.Controls;

public sealed class ErrorPanel : UserControl, IThemedControl
{
    private readonly ListBox _list;
    private readonly Panel _header;
    private readonly Label _headerLabel;
    private readonly Button _clearBtn;
    private Theme _theme = Theme.Dark;

    public event Action? Cleared;
    public event Action? EntryLogged;

    public int EntryCount => _list.Items.Count;

    public ErrorPanel()
    {
        Height = 110;
        Dock = DockStyle.Bottom;

        _list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 8.5f), IntegralHeight = false
        };

        _header = new Panel { Dock = DockStyle.Top, Height = 24 };
        _headerLabel = new Label
        {
            Text = "Playback / Scan Errors",
            Dock = DockStyle.Left,
            AutoSize = false, Width = 240,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        _clearBtn = new Button
        {
            Text = "Clear", Dock = DockStyle.Right, Width = 60,
            FlatStyle = FlatStyle.Flat, TabStop = false
        };
        _clearBtn.FlatAppearance.BorderSize = 0;
        var listRef = _list; // capture non-null
        _clearBtn.Click += (_, __) => { listRef.Items.Clear(); Cleared?.Invoke(); };
        _header.Controls.Add(_headerLabel);
        _header.Controls.Add(_clearBtn);

        Controls.Add(_list);
        Controls.Add(_header);
        ApplyTheme(_theme);
    }

    public void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        void add()
        {
            _list.Items.Insert(0, line);
            EntryLogged?.Invoke();
        }
        if (InvokeRequired) BeginInvoke(new Action(add));
        else add();
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.ErrorBack;
        _header.BackColor = theme.ErrorBar;
        _headerLabel.BackColor = theme.ErrorBar;
        _headerLabel.ForeColor = theme.Text;
        _clearBtn.BackColor = theme.ButtonBack;
        _clearBtn.ForeColor = theme.Text;
        _list.BackColor = theme.ErrorBack;
        _list.ForeColor = theme.Text;
        Invalidate(true);
    }
}
