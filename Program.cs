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

namespace MonitorOff;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\MonitorOffTrayApp", out bool firstInstance);
        if (!firstInstance) return; // already running in the tray

        ApplicationConfiguration.Initialize(); // visual styles, text rendering, PerMonitorV2 DPI from the csproj
        Application.Run(new TrayContext());
    }
}
