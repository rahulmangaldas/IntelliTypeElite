using System.Diagnostics;

namespace IntelliTypeElite;

internal static class Program
{
    private const string MouseDebounceStatusTag = "MouseDebounceStatus";

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

        var menu = new ContextMenuStrip { Renderer = new DeadRowRenderer() };
        menu.Opening += (_, _) => RefreshMouseDebounceStatus(menu, mouseDebouncer);

        menu.Items.Add("Open config", null, (_, _) =>
            Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
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

        // WinForms quirk: a ContextMenuStrip shown from a NotifyIcon decides whether to
        // flip itself above the click point (to clear the taskbar) based on its own
        // already-computed size, but it has no real size/native handle until its first
        // layout pass -- which only happens as part of being shown. Without this, the
        // very first real right-click opens flush against the bottom of the screen
        // (behind the taskbar); every later one flips correctly. Showing the fully
        // populated menu off-screen once, then immediately closing it, forces that
        // layout/handle-creation cycle before the user ever triggers a real one. This
        // goes through Opening (like a real Show), so the warm-up reflects the menu's
        // true height, not just its base items.
        menu.Show(new Point(-9999, -9999));
        menu.Close();

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

    // Fixed display order, independent of how buttons are listed in config.json.
    private static readonly string[] MouseButtonOrder = { "left", "middle", "right", "x1", "x2" };

    // Rebuilds the status rows (and trailing separator) at the top of the tray menu
    // from the live config each time the menu is about to be shown, rather than
    // pushing updates the instant config.json hot-reloads.
    private static void RefreshMouseDebounceStatus(ContextMenuStrip menu, MouseDebouncer mouseDebouncer)
    {
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i].Tag as string == MouseDebounceStatusTag)
                menu.Items.RemoveAt(i);
        }

        var rows = new List<ToolStripItem> { MakeStatusRow("Mouse debounce") };

        var config = mouseDebouncer.CurrentConfig;
        if (!config.Enabled)
        {
            rows.Add(MakeStatusRow("Off"));
        }
        else
        {
            var enabledButtons = MouseButtonOrder
                .Select(name => config.Buttons.FirstOrDefault(b => string.Equals(b.Button, name, StringComparison.OrdinalIgnoreCase)))
                .Where(b => b is { Enabled: true })
                .ToList();

            if (enabledButtons.Count == 0)
                rows.Add(MakeStatusRow("On (no buttons enabled)", indent: true));
            else
                foreach (var button in enabledButtons)
                    rows.Add(MakeStatusRow($"{DescribeButton(button!.Button)}: {button.Threshold}ms", indent: true));
        }

        rows.Add(new ToolStripSeparator { Tag = MouseDebounceStatusTag });

        for (int i = 0; i < rows.Count; i++)
            menu.Items.Insert(i, rows[i]);
    }

    // A real ToolStripMenuItem, not a ToolStripLabel -- using the same control type as
    // "Open config"/"Open log"/"Exit" is what guarantees identical text alignment,
    // rather than trying to hand-match a different control's padding to theirs.
    // Enabled = false alone still lets the default renderer paint a hover-highlight
    // box around it though, so staying fully inert relies on DeadRowRenderer below.
    //
    // indent nests a row (a per-button line, or the "no buttons enabled" fallback
    // that stands in for the button list) under the "Mouse debounce"/"Off" header
    // rows -- via a leading-space text prefix, not Padding. ToolStripMenuItem reserves
    // a fixed icon/checkmark gutter before the text, so its text actually starts at
    // max(Padding.Left, thatGutterWidth): a small Padding.Left increase on top of the
    // default gets absorbed into the gutter and produces no visible shift at all.
    private static ToolStripMenuItem MakeStatusRow(string text, bool indent = false) =>
        new(indent ? "      " + text : text) { Enabled = false, Tag = MouseDebounceStatusTag };

    // "left"/"right"/"middle" -> "Left"/"Right"/"Middle"; "x1"/"x2" -> "X1"/"X2".
    private static string DescribeButton(string button) =>
        button.Length == 2 ? button.ToUpperInvariant() : char.ToUpperInvariant(button[0]) + button[1..];

    // Suppresses the hover/selection highlight box the default renderer would
    // otherwise still draw around a disabled ToolStripMenuItem -- applied only to
    // items tagged as part of the mouse-debounce status block, so they read as fully
    // inert while everything else in the menu (Open config, Open log, Exit) keeps its
    // normal highlight.
    private sealed class DeadRowRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Tag as string == MouseDebounceStatusTag) return;
            base.OnRenderMenuItemBackground(e);
        }
    }
}
