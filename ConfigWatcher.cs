namespace IntelliTypeElite;

// Watches config.json and hot-reloads it into the running HidListener whenever it
// changes, so button/app/modifier mappings can be edited without restarting the app.
internal sealed class ConfigWatcher : IDisposable
{
    private readonly string _path;
    private readonly HidListener _listener;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _debounce;

    public ConfigWatcher(string path, HidListener listener)
    {
        _path = path;
        _listener = listener;

        // Editors often touch a file in several steps (truncate, write, rename);
        // debounce so we reload once things settle rather than mid-write.
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    private void Reload()
    {
        try
        {
            var config = RootConfig.Load(_path);
            _listener.UpdateConfig(config);
        }
        catch (Exception ex)
        {
            Logger.Write($"config.json reload failed, keeping previous config: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
