namespace IntelliTypeElite;

// Watches config.json and hot-reloads it into the running HidListener whenever it
// changes, so button/app/modifier mappings can be edited without restarting the app.
internal sealed class ConfigWatcher : IDisposable
{
    private readonly string _path;
    private readonly HidListener _listener;
    private readonly MouseDebouncer _mouseDebouncer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _debounce;

    public ConfigWatcher(string path, HidListener listener, MouseDebouncer mouseDebouncer)
    {
        _path = path;
        _listener = listener;
        _mouseDebouncer = mouseDebouncer;

        // Editors often touch a file in several steps (truncate, write, rename);
        // debounce so we reload once things settle rather than mid-write.
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        string configDir = Path.GetDirectoryName(path)!;
        string fileName = Path.GetFileName(path);

        _watchers.Add(CreateWatcher(configDir, fileName));

        // configDir may be reached through a reparse point (e.g. a junction redirecting
        // %USERPROFILE% to another drive -- see CLAUDE.md). A FileSystemWatcher opened on
        // that path doesn't reliably see changes written straight to the real target
        // directory, so also watch the resolved real path when it differs.
        string? realDir = ResolveRealDirectory(configDir);
        if (realDir is not null && !string.Equals(realDir, configDir, StringComparison.OrdinalIgnoreCase))
            _watchers.Add(CreateWatcher(realDir, fileName));
    }

    private FileSystemWatcher CreateWatcher(string dir, string fileName)
    {
        var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        watcher.Created += (_, _) => _debounce.Change(300, Timeout.Infinite);
        watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    // Walks up from dir looking for a reparse-point ancestor (junction/symlink) and, if
    // found, resolves it to build dir's real, physical path.
    private static string? ResolveRealDirectory(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            if (d.LinkTarget is null) continue;

            var target = Directory.ResolveLinkTarget(d.FullName, returnFinalTarget: true);
            if (target is null) continue;

            string relative = Path.GetRelativePath(d.FullName, dir);
            return relative == "." ? target.FullName : Path.Combine(target.FullName, relative);
        }
        return null;
    }

    private void Reload()
    {
        RootConfig config;
        try
        {
            config = RootConfig.Load(_path);
        }
        catch (Exception ex)
        {
            Logger.Write($"config.json reload failed, keeping previous config: {ex.Message}");
            return;
        }

        try { _listener.UpdateConfig(config); }
        catch (Exception ex) { Logger.Write($"HidListener.UpdateConfig failed: {ex.Message}"); }

        try { _mouseDebouncer.UpdateConfig(config.MouseDebounce); }
        catch (Exception ex) { Logger.Write($"MouseDebouncer.UpdateConfig failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _debounce.Dispose();
    }
}
