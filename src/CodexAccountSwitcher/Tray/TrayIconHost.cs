using System.Drawing;
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
        _icon = LoadApplicationIcon();

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

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        using var extracted = Icon.ExtractAssociatedIcon(executablePath)
            ?? throw new InvalidOperationException("The embedded application icon is unavailable.");
        return (Icon)extracted.Clone();
    }
}
