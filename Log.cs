namespace MonitorOff;

/// <summary>
/// Tiny append-only log at %LOCALAPPDATA%\MonitorOff\MonitorOff.log.
/// Records when the displays are turned off and what woke them, to help track down wake sources.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock s_lock = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorOff", "MonitorOff.log");

    public static void Write(string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
