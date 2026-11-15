using Microsoft.Win32;

namespace MonitorOff;

/// <summary>Owns the tray icon and its menu for the lifetime of the app.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MonitorOff";

    private readonly NotifyIcon _tray;
    private readonly BlankController _blank = new();
    private readonly ToolStripMenuItem _startupItem;
    private readonly Icon _icon = TrayIconFactory.Create();

    public TrayContext()
    {
        _startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        _startupItem.Click += (_, _) => ToggleStartup();

        var turnOff = new ToolStripMenuItem("Turn off monitors  (Esc to wake)");
        turnOff.Font = new Font(turnOff.Font, FontStyle.Bold);
        turnOff.Click += (_, _) => _blank.Start();

        var openLog = new ToolStripMenuItem("Open log (what woke the screens)");
        openLog.Click += (_, _) => OpenLog();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([turnOff, new ToolStripSeparator(), openLog, _startupItem, exit]);

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Monitor Off: click to turn off screens",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _blank.Start();
        };
    }

    protected override void ExitThreadCore()
    {
        _blank.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private static void OpenLog()
    {
        if (!File.Exists(Log.FilePath))
            Log.Write("Log created. Turn the monitors off from the tray icon, and wake-ups will be recorded here.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string;
    }

    private void ToggleStartup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (IsStartupEnabled())
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            else
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        }
        _startupItem.Checked = IsStartupEnabled();
    }
}
