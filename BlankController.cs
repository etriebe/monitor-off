using System.Runtime.InteropServices;

namespace MonitorOff;

/// <summary>Turns the monitors off and keeps them off until Esc is pressed.</summary>
internal sealed unsafe class BlankController : IDisposable
{
    // Delay before the first power-off, so releasing the tray click doesn't immediately wake them.
    private const int InitialDelayMs = 600;
    // After input or a display wake, wait this long with nothing happening before powering off again.
    private const int QuietBeforeReblankMs = 1500;

    // Low-level hook callbacks are static ([UnmanagedCallersOnly]), so they reach the
    // active controller through this field. Only one controller exists per process.
    private static BlankController? s_current;

    private readonly List<CoverForm> _covers = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly DisplayStateWatcher _displayWatcher;

    private nint _keyboardHook;
    private nint _mouseHook;
    private volatile bool _active;
    private volatile bool _needsPowerOff;
    private bool _poweredOffOnce;
    private long _lastInputTick;   // Environment.TickCount64 of the last intercepted input
    private long _lastWakeTick;    // Environment.TickCount64 when Windows last reported the display on
    private long _startTick;
    private volatile int _lastInputKind; // see InputKind
    private volatile int _lastInputDetail;
    private int _inputCount;
    private bool _cursorHidden;

    private enum InputKind { None, Key, MouseMove, MouseButtonOrWheel }

    public BlankController()
    {
        s_current = this;
        _timer.Tick += OnTimerTick;
        _displayWatcher = new DisplayStateWatcher(OnDisplayStateChanged);
    }

    public void Start()
    {
        if (_active) return;
        _active = true;

        _startTick = Environment.TickCount64;
        Interlocked.Exchange(ref _lastInputTick, _startTick);
        _lastWakeTick = 0;
        _poweredOffOnce = false;
        _inputCount = 0;
        _lastInputKind = (int)InputKind.None;

        ShowCovers();
        InstallHooks();

        _needsPowerOff = true;
        _timer.Start();
        Log.Write($"--- Blank started on {_covers.Count} screen(s)");
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
        Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, -1);
        Native.mouse_event(Native.MOUSEEVENTF_MOVE, 1, 0, 0, 0);
        Native.mouse_event(Native.MOUSEEVENTF_MOVE, -1, 0, 0, 0);
        Log.Write($"--- Esc pressed, blank stopped ({_inputCount} other input events were blocked)");
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        _displayWatcher.Dispose();
        if (s_current == this) s_current = null;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_active) return;

        KeepCoversOnTop();

        if (!_needsPowerOff) return;

        long now = Environment.TickCount64;
        long sinceStart = now - _startTick;
        if (sinceStart < InitialDelayMs) return;

        // For the first power-off, ignore input from the tray click itself.
        bool inputQuiet = sinceStart < QuietBeforeReblankMs
                          || now - Interlocked.Read(ref _lastInputTick) >= QuietBeforeReblankMs;
        bool wakeQuiet = now - _lastWakeTick >= QuietBeforeReblankMs;

        if (inputQuiet && wakeQuiet)
        {
            _needsPowerOff = false;
            PowerOffMonitors();
        }
    }

    private void PowerOffMonitors()
    {
        // Send to our own cover window: DefWindowProc handles SC_MONITORPOWER, and unlike
        // broadcasting with SendMessage, this can't hang on another app's unresponsive window.
        if (_covers.Count > 0)
            Native.SendMessage(_covers[0].Handle, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, 2);
        else
            Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, 2);

        Log.Write(_poweredOffOnce ? "Display off requested again" : "Display off requested");
        _poweredOffOnce = true;
    }

    /// <summary>Windows reports display on/off/dimmed through GUID_CONSOLE_DISPLAY_STATE.</summary>
    private void OnDisplayStateChanged(int state)
    {
        string name = state switch { 0 => "OFF", 1 => "ON", 2 => "DIMMED", _ => state.ToString() };

        if (!_active)
            return;

        if (state != 1)
        {
            Log.Write($"Windows reports display {name}");
            return;
        }

        // The display came back on while we're supposed to be blank. Figure out whether our
        // intercepted input explains it, then schedule another power-off.
        long now = Environment.TickCount64;
        long sinceInput = now - Interlocked.Read(ref _lastInputTick);
        string cause;
        if (!_poweredOffOnce)
            cause = "(still on from before the first power-off)";
        else if (_lastInputKind != (int)InputKind.None && sinceInput < 3000)
            cause = $"most recent blocked input {sinceInput} ms earlier: {DescribeLastInput()}. " +
                    "If this keeps happening with no one at the desk, that device is the culprit.";
        else
            cause = "NO mouse/keyboard input in the last 3 s, so an app, driver or other device woke the display. " +
                    "Run 'powercfg /requests' in an admin terminal while it's happening to see who.";

        Log.Write($"Windows reports display ON {cause}");

        _lastWakeTick = now;
        _needsPowerOff = true;
    }

    private string DescribeLastInput() => (InputKind)_lastInputKind switch
    {
        InputKind.Key => $"key press (virtual-key code 0x{_lastInputDetail:X2})",
        InputKind.MouseMove => "mouse movement",
        InputKind.MouseButtonOrWheel => $"mouse button/wheel (message 0x{_lastInputDetail:X4})",
        _ => "none",
    };

    // ---- Input interception -----------------------------------------------------------------

    private void InstallHooks()
    {
        nint module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, &KeyboardHookProc, module, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, &MouseHookProc, module, 0);
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != 0) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        if (_mouseHook != 0) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardHookProc(int nCode, nint wParam, nint lParam)
    {
        var self = s_current;
        if (nCode >= 0 && self is { _active: true })
        {
            int msg = (int)wParam;
            bool keyDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            int vkCode = ((Native.KBDLLHOOKSTRUCT*)lParam)->vkCode;

            if (keyDown && vkCode == Native.VK_ESCAPE)
            {
                // Leave the hook quickly; do the real work on the UI thread.
                self._covers[0].BeginInvoke(self.Stop);
                return 1;
            }

            self.NoteInput(InputKind.Key, vkCode);
            return 1; // swallow every other key while the screens are off
        }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint MouseHookProc(int nCode, nint wParam, nint lParam)
    {
        var self = s_current;
        if (nCode >= 0 && self is { _active: true })
        {
            int msg = (int)wParam;
            self.NoteInput(msg == Native.WM_MOUSEMOVE ? InputKind.MouseMove : InputKind.MouseButtonOrWheel, msg);
            return 1; // swallow movement, clicks and wheel while the screens are off
        }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private void NoteInput(InputKind kind, int detail)
    {
        Interlocked.Exchange(ref _lastInputTick, Environment.TickCount64);
        _lastInputKind = (int)kind;
        _lastInputDetail = detail;
        Interlocked.Increment(ref _inputCount);
        _needsPowerOff = true; // re-blank once things go quiet, in case the input woke a display
    }

    // ---- Black cover windows ----------------------------------------------------------------

    private void ShowCovers()
    {
        CoverForm? primary = null;
        foreach (Screen screen in Screen.AllScreens)
        {
            var cover = new CoverForm(screen.Bounds);
            cover.Show();
            _covers.Add(cover);
            if (screen.Primary) primary = cover;
        }

        // The taskbar only hides itself when a fullscreen window on its monitor is the
        // *foreground* window, so activate the primary cover. (Keyboard input is swallowed
        // by the hook anyway, so having focus here does no harm.)
        if (primary is not null)
        {
            primary.Activate();
            Native.SetForegroundWindow(primary.Handle);
        }

        if (!_cursorHidden) { Cursor.Hide(); _cursorHidden = true; }
    }

    private void KeepCoversOnTop()
    {
        foreach (var cover in _covers)
        {
            Native.SetWindowPos(cover.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }
    }

    private void CloseCovers()
    {
        if (_cursorHidden) { Cursor.Show(); _cursorHidden = false; }
        foreach (var cover in _covers) { cover.Close(); cover.Dispose(); }
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

        // Shown without activation; the primary cover is activated explicitly afterwards.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }
    }

    // ---- Display state notifications ---------------------------------------------------------

    /// <summary>
    /// Hidden window that receives GUID_CONSOLE_DISPLAY_STATE power notifications, so we know
    /// whenever Windows turns the displays back on, whatever caused it.
    /// </summary>
    private sealed class DisplayStateWatcher : NativeWindow, IDisposable
    {
        private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
        private readonly Action<int> _onChanged;
        private nint _registration;

        public DisplayStateWatcher(Action<int> onChanged)
        {
            _onChanged = onChanged;
            CreateHandle(new CreateParams()); // plain hidden top-level window (message-only windows don't get broadcasts)
            Guid guid = GUID_CONSOLE_DISPLAY_STATE;
            _registration = Native.RegisterPowerSettingNotification(Handle, &guid, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (_registration == 0)
                Log.Write("Warning: could not register for display state notifications");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_POWERBROADCAST && (int)m.WParam == Native.PBT_POWERSETTINGCHANGE && m.LParam != 0)
            {
                var setting = (Native.POWERBROADCAST_SETTING*)m.LParam;
                if (setting->PowerSetting == GUID_CONSOLE_DISPLAY_STATE && setting->DataLength >= 4)
                    _onChanged(*(int*)&setting->Data);
                m.Result = 1;
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_registration != 0) { Native.UnregisterPowerSettingNotification(_registration); _registration = 0; }
            DestroyHandle();
        }
    }
}
