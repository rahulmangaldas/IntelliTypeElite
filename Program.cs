using System.Diagnostics;

namespace IntelliTypeElite;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Logger.Enabled = args.Any(a => a.Equals("/log", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();

        string configPath = ResolveConfigPath();
        var config = RootConfig.Load(configPath);

        var listener = new HidListener(config);
        listener.Start();

        using var mouseDebouncer = new MouseDebouncer(config.MouseDebounce);
        mouseDebouncer.Start();

        using var configWatcher = new ConfigWatcher(configPath, listener, mouseDebouncer);

        using var trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "IntelliType Elite",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        if (Logger.Enabled)
        {
            menu.Items.Add("Open log", null, (_, _) =>
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "IntelliTypeElite.log");
                if (File.Exists(logPath))
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            });
        }
        menu.Items.Add("Exit", null, (_, _) =>
        {
            listener.Stop();
            Application.Exit();
        });
        trayIcon.ContextMenuStrip = menu;

        // Only installed once a window handle exists (the NotifyIcon's, created above) --
        // capturing it any earlier (e.g. right after ApplicationConfiguration.Initialize())
        // would leave this null, since WindowsFormsSynchronizationContext isn't set up until
        // then.
        UiThread.Context = SynchronizationContext.Current;

        Application.Run();
    }

    // config.json lives under $XDG_CONFIG_HOME (default ~/.config), not next to the
    // exe -- keeps user edits separate from the install/build output. Seeded from the
    // bundled default (shipped alongside the exe) on first run.
    //
    // Environment.SpecialFolder.UserProfile always resolves to the real Windows
    // profile, which on this machine is C:\ (periodically wiped). That's handled at
    // the OS level, not here: C:\Users\<name>\.config is a directory junction to the
    // real, persistent .config on D:\ -- see CLAUDE.md.
    private static string ResolveConfigPath()
    {
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        string configDir = Path.Combine(configHome, "IntelliTypeElite");
        Directory.CreateDirectory(configDir);

        string configPath = Path.Combine(configDir, "config.json");
        if (!File.Exists(configPath))
            File.Copy(Path.Combine(AppContext.BaseDirectory, "config.json"), configPath);

        // Unlike config.json (user edits, never overwritten), the schema is
        // app-internal and versioned with the exe -- always refresh it so an
        // existing install picks up new fields (e.g. mouseDebounce) without the
        // editor flagging them as unknown.
        string schemaSource = Path.Combine(AppContext.BaseDirectory, "config.schema.json");
        if (File.Exists(schemaSource))
            File.Copy(schemaSource, Path.Combine(configDir, "config.schema.json"), overwrite: true);

        return configPath;
    }
}
