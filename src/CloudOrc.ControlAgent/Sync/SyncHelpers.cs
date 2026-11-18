using System.Globalization;
using System.Runtime.InteropServices;
using CloudOrc.Agent.Contracts.Sync;
using Microsoft.Win32.SafeHandles;

namespace CloudOrc.ControlAgent.Sync;

/// <summary>
/// Stable local file identity (spec §08 "stable local file ID where possible"): NTFS volume serial +
/// 64-bit file index, which survives renames/moves within the volume. Null when unavailable.
/// </summary>
public static class StableFileId
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    public static string? TryGet(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid)
        {
            return null;
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                return null;
            }

            var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return $"{info.VolumeSerialNumber:x8}:{index:x16}";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>Time-of-day bandwidth windows (spec §08 "bandwidth windows").</summary>
public static class SyncBandwidth
{
    /// <summary>The first window that matches the local day and time, or null.</summary>
    public static SyncBandwidthWindow? ActiveWindow(IReadOnlyList<SyncBandwidthWindow>? windows, DateTimeOffset localNow)
    {
        if (windows is null || windows.Count == 0)
        {
            return null;
        }

        var day = localNow.DayOfWeek.ToString()[..3];
        var minute = localNow.Hour * 60 + localNow.Minute;
        foreach (var w in windows)
        {
            if (w.Days.Count > 0 && !w.Days.Any(d => d.Length >= 3 && string.Equals(d[..3], day, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var start = ParseMinute(w.Start, 0);
            var end = ParseMinute(w.End, 24 * 60);
            var inside = start <= end ? minute >= start && minute < end : minute >= start || minute < end; // over midnight
            if (inside)
            {
                return w;
            }
        }

        return null;
    }

    private static int ParseMinute(string? value, int fallback)
    {
        if (value == "24:00")
        {
            return 24 * 60;
        }

        return TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? (int)t.TotalMinutes : fallback;
    }
}
