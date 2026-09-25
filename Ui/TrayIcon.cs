using System.Drawing;
using System.Windows.Forms;

namespace HdrCapture.Ui;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _editLastItem;
    private Icon? _icon;
    private bool _disposed;

    public TrayIcon(string hotkeyText)
    {
        _captureItem = new ToolStripMenuItem();
        _editLastItem = new ToolStripMenuItem("编辑上次截图", null, (_, _) => EditLastRequested?.Invoke(this, EventArgs.Empty))
        {
            Enabled = false
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_captureItem);
        menu.Items.Add(_editLastItem);
        menu.Items.Add(new ToolStripMenuItem("打开保存目录", null, (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem("设置...", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        menu.Opening += (_, _) => _captureItem.Text = $"截图（{_hotkeyText}）";

        _icon = CreateIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "HDRCapture - HDR 区域截图",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => CaptureRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                CaptureRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        _hotkeyText = hotkeyText;
    }

    private string _hotkeyText;

    public event EventHandler? CaptureRequested;

    public event EventHandler? EditLastRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? OpenFolderRequested;

    public event EventHandler? ExitRequested;

    public void UpdateHotkey(string hotkeyText) => _hotkeyText = hotkeyText;

    public void SetHasCapture(bool hasCapture) => _editLastItem.Enabled = hasCapture;

    public void ShowError(string message) =>
        Show("HDRCapture 出错", message, ToolTipIcon.Error);

    public void ShowInfo(string message) =>
        Show("HDRCapture", message, ToolTipIcon.Info);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        _icon = null;
    }

    private void Show(string title, string message, ToolTipIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, message, icon);
        }
        catch
        {
        }
    }

    private static Icon CreateIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var extracted = Icon.ExtractAssociatedIcon(executable);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
