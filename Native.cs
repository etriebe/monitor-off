using System.Runtime.InteropServices;

namespace MonitorOff;

/// <summary>Win32 interop, using source-generated LibraryImport (no runtime marshalling stubs).</summary>
internal static unsafe partial class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    public const int SC_MONITORPOWER = 0xF170;
    public const int VK_ESCAPE = 0x1B;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const nint HWND_BROADCAST = 0xFFFF;
    public const nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // first byte of a variable-length buffer
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(
        int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nuint dwExtraInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint RegisterPowerSettingNotification(nint hRecipient, Guid* powerSettingGuid, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(nint handle);
}
