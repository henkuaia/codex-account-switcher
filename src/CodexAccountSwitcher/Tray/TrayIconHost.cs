using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CodexAccountSwitcher.Tray;

public sealed class TrayIconHost : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Action _openAction;
    private readonly Action _exitAction;
    private bool _disposed;

    public TrayIconHost(Action openAction, Action exitAction)
    {
        _openAction = openAction ?? throw new ArgumentNullException(nameof(openAction));
        _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));
        _icon = CreateSwitchIcon();

        var openItem = new Forms.ToolStripMenuItem("Open");
        openItem.Click += (_, _) => _openAction();
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Exit();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(openItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "Codex account switcher",
            Visible = false,
        };
        _notifyIcon.DoubleClick += (_, _) => _openAction();
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifyIcon.Visible = true;
    }

    public void Hide()
    {
        if (!_disposed)
        {
            _notifyIcon.Visible = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Exit()
    {
        _exitAction();
    }

    private static Icon CreateSwitchIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(Color.FromArgb(45, 102, 120), 3.2f))
        using (var brush = new SolidBrush(Color.FromArgb(45, 102, 120)))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            graphics.DrawLine(pen, 7, 11, 23, 11);
            graphics.FillPolygon(brush, [new Point(23, 6), new Point(29, 11), new Point(23, 16)]);
            graphics.DrawLine(pen, 25, 21, 9, 21);
            graphics.FillPolygon(brush, [new Point(9, 16), new Point(3, 21), new Point(9, 26)]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
