namespace CloudOrc.WorkspaceUI.Services;

public enum WindowState2
{
    Active,
    Running,
    Minimized
}

/// <summary>One visible top-level window in this Windows session (handoff §44 "Window discovery").</summary>
public sealed record TopWindow(IntPtr Handle, int ProcessId, string? ExePath, string Title, WindowState2 State);

/// <summary>Windows grouped under one app (RW-02: "TallyPrime ×3").</summary>
public sealed class AppWindowGroup
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? PublishedAppId { get; init; }
    public string? ExePath { get; init; }
    public required IReadOnlyList<TopWindow> Windows { get; init; }

    public int Count => Windows.Count;
    public bool IsPublished => PublishedAppId is not null;
    public bool AnyActive => Windows.Any(w => w.State == WindowState2.Active);
}

/// <summary>
/// Pure grouping (unit-tested): identity is the executable path mapped to a publishedAppId — the
/// window title is display text only, never identity (§44).
/// </summary>
public static class WindowGrouping
{
    private static string FolderKey(string? path) => (path ?? string.Empty).Trim().Trim('"').TrimEnd('\\').Trim();

    public static IReadOnlyList<AppWindowGroup> Group(IEnumerable<TopWindow> windows, IReadOnlyList<PublishedApp> published)
    {
        var byExe = new Dictionary<string, PublishedApp>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, PublishedApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in published.Where(a => !a.IsFolder && !string.IsNullOrWhiteSpace(a.ExePath)))
        {
            var full = Normalize(app.ExePath);
            byExe.TryAdd(full, app);
            byFileName.TryAdd(Path.GetFileName(full), app);
        }

        // TSPlus folders all open through the same launcher (folder.exe): its window title is the folder path,
        // so a folder window belongs to the published folder entry whose cmdline is that path.
        var byFolderPath = new Dictionary<string, PublishedApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in published.Where(a => a.IsFolder && TsplusAppSource.IsFolderLauncher(a.ExePath)))
        {
            byFolderPath.TryAdd(FolderKey(app.Arguments), app);
        }

        var groups = new Dictionary<string, (string Name, PublishedApp? App, string? Exe, List<TopWindow> Windows)>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            PublishedApp? app = null;
            if (w.ExePath is { Length: > 0 } exe)
            {
                var n = Normalize(exe);
                if (TsplusAppSource.IsFolderLauncher(n))
                {
                    byFolderPath.TryGetValue(FolderKey(w.Title), out app);
                }
                else if (!byExe.TryGetValue(n, out app))
                {
                    // Same exe name in a different folder (e.g. Tally copied per company) still maps.
                    byFileName.TryGetValue(Path.GetFileName(n), out app);
                }
            }

            var key = app is not null ? "app:" + app.Id : "exe:" + (w.ExePath is { Length: > 0 } ? Normalize(w.ExePath) : "pid:" + w.ProcessId);
            var name = app?.Name ?? FriendlyName(w.ExePath) ?? w.Title;
            if (!groups.TryGetValue(key, out var g))
            {
                g = (name, app, w.ExePath, new List<TopWindow>());
                groups[key] = g;
            }

            g.Windows.Add(w);
        }

        return groups
            .Select(kv => new AppWindowGroup
            {
                Key = kv.Key,
                Name = kv.Value.Name,
                PublishedAppId = kv.Value.App?.Id,
                ExePath = kv.Value.Exe,
                Windows = kv.Value.Windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList()
            })
            .OrderByDescending(g => g.IsPublished)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>"C:\Tally\tally.exe" -&gt; "Tally" when no published name is known.</summary>
    public static string? FriendlyName(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(exePath);
        return name.Length == 0 ? null : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Display title for an instance: strips a trailing " - AppName" so "ABC Traders - TallyPrime" shows "ABC Traders".</summary>
    public static string InstanceTitle(string title, string appName)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return appName;
        }

        foreach (var sep in new[] { " - ", " – ", " | " })
        {
            var suffix = sep + appName;
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && title.Length > suffix.Length)
            {
                return title[..^suffix.Length];
            }
        }

        return title;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }
}
