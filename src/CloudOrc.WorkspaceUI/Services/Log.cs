namespace CloudOrc.WorkspaceUI.Services;

/// <summary>Tiny per-user file log (%LocalAppData%\CloudOrc\WorkspaceUI\logs). Never logs tokens or secrets.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudOrc", "WorkspaceUI", "logs");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = Path.Combine(Directory, $"workspaceui-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the panel.
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    /// <summary>Deletes log files older than 14 days.</summary>
    public static void Prune()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var f in System.IO.Directory.GetFiles(Directory, "workspaceui-*.log"))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
            }
        }
        catch
        {
            // best effort
        }
    }
}
