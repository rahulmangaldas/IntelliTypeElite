using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IntelliTypeElite;

// Removes the Mark of the Web (the Zone.Identifier alternate data stream) from files of
// "safe" types in configured folders, in real time (FileSystemWatcher) plus a periodic
// sweep. Hot-reloadable via UpdateConfig, same shape as MouseDebouncer.
//
// Safe default: only extensions listed in config are touched, so executables, scripts,
// Office files and archives keep their mark (SmartScreen / Protected View still apply).
// Read-only files are deliberately left marked: deleting their stream is denied, and
// clearing the read-only flag to get around that would change metadata OneDrive syncs.
internal sealed class FileUnblocker : IDisposable
{
    private const string StreamSuffix = ":Zone.Identifier";
    private const int MaxAttempts = 5;
    private const int RetryDelayMs = 2000;
    private const int MaxSettleMs = 60_000;
    private const int MaxSweepIntervalMinutes = 7 * 24 * 60; // System.Threading.Timer tops out near 71,582 min
    private const int MaxLoggedDenied = 1000;

    // OneDrive / cloud-files placeholders. Touching these can trigger a download, and a
    // cloud-only file can't carry a local mark anyway until it is hydrated.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    private enum Result { Unblocked, NoMark, Skipped, Retry }

    // Wrapper so Run can remove *its own* pending entry without clobbering a newer timer
    // that Schedule installed for the same path in the meantime.
    private sealed class Pending
    {
        public System.Threading.Timer? Timer;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deniedLogged = new(StringComparer.OrdinalIgnoreCase);

    private FileUnblockerConfig _config = new();
    private string _signature = "";
    private List<string> _roots = new();
    private HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _sweepTimer;
    private int _sweepRunning;
    private int _sweepRequested;
    private bool _disposed;

    // Applies a (possibly changed) config. A no-op when the normalized section is identical
    // to what's already running, so editing unrelated parts of config.json doesn't restart
    // watchers or trigger another full sweep. Never throws: a bad section leaves the feature
    // off (and is retried on the next reload) rather than taking the app down.
    public void UpdateConfig(FileUnblockerConfig? raw)
    {
        FileUnblockerConfig config = Normalize(raw);
        string signature = JsonSerializer.Serialize(config);
        bool sweep = false;

        lock (_gate)
        {
            if (_disposed || signature == _signature) return;

            Teardown();
            _signature = ""; // only committed once everything below has been built
            _config = new FileUnblockerConfig();

            try
            {
                if (!config.Enabled)
                {
                    _signature = signature;
                    Logger.Write("FileUnblocker: disabled");
                    return;
                }

                _config = config;
                _extensions = new HashSet<string>(
                    config.Extensions.Select(e => e.StartsWith('.') ? e : "." + e),
                    StringComparer.OrdinalIgnoreCase);
                _roots = ResolveRoots(config);
                EnsureWatchersLocked();

                if (config.SweepIntervalMinutes > 0)
                {
                    var period = TimeSpan.FromMinutes(config.SweepIntervalMinutes);
                    _sweepTimer = new System.Threading.Timer(_ => SweepNow(), null, period, period);
                }

                _signature = signature;
                sweep = config.SweepOnStart;
                Logger.Write($"FileUnblocker: watching {_roots.Count} folder(s), {_extensions.Count} extension(s)");
            }
            catch (Exception ex)
            {
                Teardown();
                _config = new FileUnblockerConfig();
                Logger.Write($"FileUnblocker: config rejected, feature off until it changes: {ex.Message}");
                return;
            }
        }

        if (sweep) Task.Run(SweepNow);
    }

    // Clamps and null-proofs a section as read from JSON (System.Text.Json happily sets a
    // list, or the whole section, to null, and nothing validates numeric ranges).
    private static FileUnblockerConfig Normalize(FileUnblockerConfig? raw)
    {
        raw ??= new FileUnblockerConfig();
        var defaults = new FileUnblockerConfig();
        return new FileUnblockerConfig
        {
            Enabled = raw.Enabled,
            Folders = (raw.Folders ?? new List<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList(),
            // A missing list means "use the defaults"; an explicit empty list means nothing.
            Extensions = raw.Extensions is null
                ? defaults.Extensions
                : raw.Extensions.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
            IncludeSubfolders = raw.IncludeSubfolders,
            SettleMs = Math.Clamp(raw.SettleMs, 0, MaxSettleMs),
            SweepOnStart = raw.SweepOnStart,
            SweepIntervalMinutes = Math.Clamp(raw.SweepIntervalMinutes, 0, MaxSweepIntervalMinutes),
        };
    }

    // One full pass over every configured folder. Also the "Unblock files now" tray item.
    // A request that arrives while a sweep is running is not dropped: the running sweep
    // loops once more, so e.g. a newly added folder is picked up right after.
    public void SweepNow()
    {
        Volatile.Write(ref _sweepRequested, 1);
        if (Interlocked.Exchange(ref _sweepRunning, 1) == 1) return; // the runner will see the request

        try
        {
            while (Interlocked.Exchange(ref _sweepRequested, 0) == 1)
                SweepOnce();
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
            // A request that landed after the loop's last check but before the line above.
            if (Volatile.Read(ref _sweepRequested) == 1) Task.Run(SweepNow);
        }
    }

    private void SweepOnce()
    {
        try
        {
            List<string> roots;
            bool recurse;
            lock (_gate)
            {
                if (_disposed || !_config.Enabled) return;
                EnsureWatchersLocked(); // revive watchers that died (folder deleted/recreated, etc.)
                roots = new List<string>(_roots);
                recurse = _config.IncludeSubfolders;
            }

            int unblocked = 0;
            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in EnumerateCandidates(root, recurse))
                    {
                        if (TryUnblock(file) == Result.Unblocked) unblocked++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Write($"FileUnblocker: sweep of '{root}' failed: {ex.Message}");
                }
            }
            Logger.Write($"FileUnblocker: sweep done, unblocked {unblocked} file(s)");
        }
        catch (Exception ex)
        {
            Logger.Write($"FileUnblocker: sweep failed: {ex.Message}");
        }
    }

    // Creates a watcher for every root that doesn't have a live one. Caller holds _gate.
    private void EnsureWatchersLocked()
    {
        foreach (string root in _roots)
        {
            if (_watchers.ContainsKey(root) || !Directory.Exists(root)) continue;
            try { _watchers[root] = CreateWatcher(root, _config.IncludeSubfolders); }
            catch (Exception ex) { Logger.Write($"FileUnblocker: cannot watch '{root}': {ex.Message}"); }
        }
    }

    private FileSystemWatcher CreateWatcher(string root, bool recurse)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = recurse,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnFileEvent(e.FullPath);
        watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Error += (_, e) => OnWatcherError(root, watcher, e.GetException());
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    // Event handlers run on threadpool threads: an escaping exception would end the process.
    private void OnFileEvent(string path)
    {
        try { Schedule(path, _config.SettleMs, 0); }
        catch (Exception ex) { Logger.Write($"FileUnblocker: event error on '{path}': {ex.Message}"); }
    }

    private void OnWatcherError(string root, FileSystemWatcher watcher, Exception error)
    {
        try
        {
            Logger.Write($"FileUnblocker: watcher error on '{root}': {error.Message}");

            // Overflow: the watcher is still alive but events were lost; a sweep catches up.
            // Anything else (folder deleted/renamed, access lost) stops the watcher for good,
            // so drop it -- the sweep below recreates it once the folder is back.
            if (error is not InternalBufferOverflowException)
            {
                lock (_gate)
                {
                    if (_watchers.TryGetValue(root, out var current) && ReferenceEquals(current, watcher))
                    {
                        _watchers.Remove(root);
                        watcher.Dispose();
                    }
                }
            }

            Task.Run(SweepNow);
        }
        catch (Exception ex)
        {
            Logger.Write($"FileUnblocker: watcher error handler failed: {ex.Message}");
        }
    }

    // (Re)starts the per-file timer. Downloads touch a file several times (write, rename,
    // stamp the mark), so every event pushes the deadline out and the file is only handled
    // once it has been quiet for the settle time.
    private void Schedule(string path, int delayMs, int attempt)
    {
        if (!IsCandidate(path)) return;

        lock (_gate)
        {
            if (_disposed || !_config.Enabled) return;

            if (_pending.TryRemove(path, out var old)) old.Timer?.Dispose();

            var pending = new Pending();
            pending.Timer = new System.Threading.Timer(_ => Run(path, attempt, pending), null,
                Timeout.Infinite, Timeout.Infinite);
            _pending[path] = pending;
            pending.Timer.Change(delayMs, Timeout.Infinite);
        }
    }

    private void Run(string path, int attempt, Pending self)
    {
        try
        {
            // Not ours any more (a newer event replaced us, or the config was torn down):
            // the newer timer, if any, will handle the file.
            if (!_pending.TryRemove(new KeyValuePair<string, Pending>(path, self))) return;
            self.Timer?.Dispose();

            if (!_config.Enabled) return;

            Result result = TryUnblock(path);
            if (result == Result.Retry && attempt + 1 < MaxAttempts)
                Schedule(path, RetryDelayMs, attempt + 1);
        }
        catch (Exception ex)
        {
            Logger.Write($"FileUnblocker: error on '{path}': {ex.Message}");
        }
    }

    private Result TryUnblock(string path)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); }
        catch (FileNotFoundException) { return Result.Skipped; }
        catch (DirectoryNotFoundException) { return Result.Skipped; }
        catch (UnauthorizedAccessException) { return Result.Skipped; }
        catch (IOException) { return Result.Retry; }

        if ((attrs & FileAttributes.Directory) != 0) return Result.Skipped;
        if ((attrs & (RecallOnOpen | RecallOnDataAccess)) != 0) return Result.Skipped;

        if (DeleteFileW(ToExtendedPath(path) + StreamSuffix))
        {
            Logger.Write($"FileUnblocker: unblocked {path}");
            return Result.Unblocked;
        }

        int error = Marshal.GetLastWin32Error();
        switch (error)
        {
            case ErrorFileNotFound:
            case ErrorPathNotFound:
                return Result.NoMark; // no mark on this file -- the normal case
            case ErrorSharingViolation:
            case ErrorLockViolation:
                return Result.Retry; // still being written / opened exclusively
            default:
                // Typically ERROR_ACCESS_DENIED on a read-only file. Left marked on purpose;
                // log each path once so every sweep doesn't repeat it.
                LogDeniedOnce(path, error);
                return Result.Skipped;
        }
    }

    private void LogDeniedOnce(string path, int error)
    {
        bool first;
        lock (_deniedLogged)
        {
            first = _deniedLogged.Count < MaxLoggedDenied && _deniedLogged.Add(path);
        }
        if (first) Logger.Write($"FileUnblocker: could not remove mark (error {error}, left as is): {path}");
    }

    // The \\?\ prefix lifts MAX_PATH for this raw Win32 call (File.* does it for us).
    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        if (path.Length > 2 && path[1] == ':') return @"\\?\" + path;
        return path;
    }

    private bool IsCandidate(string path) => _extensions.Contains(Path.GetExtension(path));

    // Own walk rather than EnumerateFiles(Recursive): skips junctions/symlinks (the home
    // folder is full of junctions into OneDrive, which would be visited twice -- or loop)
    // and OneDrive cloud-only folders (listing them can trigger a download). Cloud reparse
    // points on *synced* folders are not links, so those are still entered.
    private IEnumerable<string> EnumerateCandidates(string root, bool recurse)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception) { continue; }

            foreach (var entry in entries)
            {
                bool isDir;
                bool descend = false;
                string? candidate = null;
                try
                {
                    var attrs = entry.Attributes;
                    isDir = (attrs & FileAttributes.Directory) != 0;
                    if (isDir)
                    {
                        descend = recurse
                                  && (attrs & (RecallOnOpen | RecallOnDataAccess)) == 0
                                  && !((attrs & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null);
                    }
                    else if (IsCandidate(entry.FullName))
                    {
                        candidate = entry.FullName;
                    }
                }
                catch (Exception)
                {
                    continue; // unreadable entry: skip it, keep sweeping
                }

                if (descend) stack.Push(entry.FullName);
                if (candidate is not null) yield return candidate;
            }
        }
    }

    // Expands %VAR%, resolves junctions to the real path (a watcher on the junction path
    // can miss changes written to the target), drops missing folders and folders nested
    // inside another watched folder (they'd raise every event twice).
    private static List<string> ResolveRoots(FileUnblockerConfig config)
    {
        var resolved = new List<string>();
        foreach (string folder in config.Folders)
        {
            try
            {
                string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder));
                if (!Directory.Exists(full))
                {
                    Logger.Write($"FileUnblocker: folder not found, skipped: {full}");
                    continue;
                }

                string real = ConfigWatcher.ResolveRealDirectory(full) ?? full;
                real = Path.TrimEndingDirectorySeparator(real); // keeps a drive root like D:\ intact
                if (!resolved.Contains(real, StringComparer.OrdinalIgnoreCase)) resolved.Add(real);
            }
            catch (Exception ex)
            {
                Logger.Write($"FileUnblocker: bad folder '{folder}', skipped: {ex.Message}");
            }
        }

        if (!config.IncludeSubfolders) return resolved;

        return resolved
            .Where(r => !resolved.Any(other =>
                !ReferenceEquals(other, r) &&
                r.StartsWith(WithSeparator(other), StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    // Caller holds _gate (or is Dispose).
    private void Teardown()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();

        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending)) pending.Timer?.Dispose();
        }

        _sweepTimer?.Dispose();
        _sweepTimer = null;

        _roots = new List<string>();
        _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // in-flight events find nothing to match
        lock (_deniedLogged) _deniedLogged.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Teardown();
        }
    }
}
