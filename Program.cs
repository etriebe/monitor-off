// Monitor Off — a tray app that turns off every connected monitor until Esc is pressed.
//
// How it works:
//   1. Left-click the tray icon (or choose "Turn off monitors" from its menu).
//   2. A black, cursor-less window covers every screen, and all mouse and keyboard
//      input is intercepted so stray movement doesn't wake the displays or reach other apps.
//   3. Windows is told to power the monitors down (SC_MONITORPOWER).
//   4. If anything wakes them anyway, they are powered down again after a moment of quiet.
//   5. Press Esc: input is restored, the covers close and the monitors wake.
//
// Safety: Ctrl+Alt+Del can never be intercepted by an app, so it always gets you out.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorOff
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (var mutex = new Mutex(true, @"Local\MonitorOffTrayApp", out bool firstInstance))
            {
                if (!firstInstance) return; // already running in the tray

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "MonitorOff";

        private readonly NotifyIcon _tray;
        private readonly BlankController _blank;
        private readonly ToolStripMenuItem _startupItem;
        private readonly Icon _icon;

        public TrayContext()
        {
            _blank = new BlankController();
            _icon = TrayIconFactory.Create();

            _startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
            _startupItem.Click += (s, e) => ToggleStartup();

            var turnOff = new ToolStripMenuItem("Turn off monitors  (Esc to wake)");
            turnOff.Font = new Font(turnOff.Font, FontStyle.Bold);
            turnOff.Click += (s, e) => _blank.Start();

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => ExitThread();

            var menu = new ContextMenuStrip();
            menu.Items.Add(turnOff);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startupItem);
            menu.Items.Add(exit);

            _tray = new NotifyIcon
            {
                Icon = _icon,
                Text = "Monitor Off: click to turn off screens",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) _blank.Start();
            };
        }

        protected override void ExitThreadCore()
        {
            _blank.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
            base.ExitThreadCore();
        }

        private static bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                return key?.GetValue(RunValueName) is string;
        }

        private void ToggleStartup()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (IsStartupEnabled())
                    key.DeleteValue(RunValueName, false);
                else
                    key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            }
            _startupItem.Checked = IsStartupEnabled();
        }
    }

    /// <summary>Turns the monitors off and keeps them off until Esc is pressed.</summary>
    internal sealed class BlankController
    {
        // Delay before the first power-off, so releasing the tray click doesn't immediately wake them.
        private const int InitialDelayMs = 600;
        // After intercepted input, wait this long without input before powering off again.
        private const int QuietBeforeReblankMs = 1500;

        private readonly List<Form> _covers = new List<Form>();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Native.LowLevelProc _keyboardProc;
        private readonly Native.LowLevelProc _mouseProc;

        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;
        private bool _active;
        private bool _needsPowerOff;
        private long _lastInputTick;   // Environment.TickCount of last intercepted input
        private long _startTick;
        private bool _cursorHidden;

        public BlankController()
        {
            // Keep delegates in fields so the GC never collects them while hooks are installed.
            _keyboardProc = KeyboardHookProc;
            _mouseProc = MouseHookProc;
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            if (_active) return;
            _active = true;

            ShowCovers();
            InstallHooks();

            _startTick = Environment.TickCount;
            _lastInputTick = _startTick;
            _needsPowerOff = true;
            _timer.Start();
        }

        public void Stop()
        {
            if (!_active) return;
            _active = false;

            _timer.Stop();
            RemoveHooks();
            CloseCovers();

            // Wake the displays. SC_MONITORPOWER "on" is unreliable on modern Windows,
            // so also nudge the mouse by one pixel and back, which always wakes them.
            Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND,
                (IntPtr)Native.SC_MONITORPOWER, (IntPtr)(-1));
            Native.mouse_event(Native.MOUSEEVENTF_MOVE, 1, 0, 0, UIntPtr.Zero);
            Native.mouse_event(Native.MOUSEEVENTF_MOVE, -1, 0, 0, UIntPtr.Zero);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (!_active) return;

            long now = Environment.TickCount;
            bool initialDelayDone = now - _startTick >= InitialDelayMs;
            bool quiet = now - Interlocked.Read(ref _lastInputTick) >= QuietBeforeReblankMs
                         || now - _startTick < QuietBeforeReblankMs; // don't let the click's own input delay the first blank

            if (_needsPowerOff && initialDelayDone && quiet)
            {
                _needsPowerOff = false;
                PowerOffMonitors();
            }
        }

        private void PowerOffMonitors()
        {
            // Send to our own cover window: DefWindowProc handles SC_MONITORPOWER, and unlike
            // broadcasting with SendMessage, this can't hang on another app's unresponsive window.
            IntPtr target = _covers.Count > 0 ? _covers[0].Handle : Native.HWND_BROADCAST;
            if (target == Native.HWND_BROADCAST)
                Native.PostMessage(target, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MONITORPOWER, (IntPtr)2);
            else
                Native.SendMessage(target, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MONITORPOWER, (IntPtr)2);
        }

        // ---- Input interception -------------------------------------------------------------

        private void InstallHooks()
        {
            IntPtr module = Native.GetModuleHandle(null);
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        }

        private void RemoveHooks()
        {
            if (_keyboardHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
            if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _active)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool keyDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;

                if (keyDown && info.vkCode == Native.VK_ESCAPE)
                {
                    // Leave the hook quickly; do the real work on the UI thread.
                    _covers[0].BeginInvoke((Action)Stop);
                    return (IntPtr)1;
                }

                NoteInput();
                return (IntPtr)1; // swallow every other key while the screens are off
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _active)
            {
                NoteInput();
                return (IntPtr)1; // swallow movement, clicks and wheel while the screens are off
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private void NoteInput()
        {
            Interlocked.Exchange(ref _lastInputTick, Environment.TickCount);
            _needsPowerOff = true; // re-blank once things go quiet, in case the input woke a display
        }

        // ---- Black cover windows ------------------------------------------------------------

        private void ShowCovers()
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                var cover = new CoverForm(screen.Bounds);
                cover.Show();
                _covers.Add(cover);
            }
            if (!_cursorHidden) { Cursor.Hide(); _cursorHidden = true; }
        }

        private void CloseCovers()
        {
            if (_cursorHidden) { Cursor.Show(); _cursorHidden = false; }
            foreach (Form f in _covers) { f.Close(); f.Dispose(); }
            _covers.Clear();
        }

        private sealed class CoverForm : Form
        {
            public CoverForm(Rectangle bounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.Black;
                ShowInTaskbar = false;
                TopMost = true;
                Bounds = bounds;
            }

            // Don't steal focus from whatever the user was doing.
            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                    return cp;
                }
            }
        }
    }

    /// <summary>Draws a small monitor glyph so the app needs no icon file.</summary>
    internal static class TrayIconFactory
    {
        public static Icon Create()
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width) * 2; // draw large, Windows scales down cleanly
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                float u = size / 16f;
                using (var frame = new SolidBrush(Color.FromArgb(235, 235, 235)))
                using (var screen = new SolidBrush(Color.FromArgb(20, 20, 20)))
                using (var moon = new SolidBrush(Color.FromArgb(120, 170, 255)))
                {
                    // Bezel and screen
                    g.FillRectangle(frame, 1 * u, 2 * u, 14 * u, 9.5f * u);
                    g.FillRectangle(screen, 2.2f * u, 3.2f * u, 11.6f * u, 7.1f * u);
                    // Stand
                    g.FillRectangle(frame, 7 * u, 11.5f * u, 2 * u, 1.8f * u);
                    g.FillRectangle(frame, 4.5f * u, 13.3f * u, 7 * u, 1.4f * u);
                    // Crescent moon = "off / sleeping"
                    g.FillEllipse(moon, 5.6f * u, 4.2f * u, 4.8f * u, 4.8f * u);
                    g.FillEllipse(screen, 7.2f * u, 3.6f * u, 4.4f * u, 4.4f * u);
                }

                IntPtr hIcon = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(hIcon))
                {
                    var icon = (Icon)tmp.Clone();
                    Native.DestroyIcon(hIcon);
                    return icon;
                }
            }
        }
    }

    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public const int VK_ESCAPE = 0x1B;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
