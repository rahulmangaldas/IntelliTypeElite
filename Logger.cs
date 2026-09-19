namespace IntelliTypeElite;

// Disabled by default -- only writes when launched with /log, per user
// preference that the normal binary stays silent and doesn't create files
// unasked. Program.cs sets Enabled before starting the listener.
internal static class Logger
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "keymapper.log");
    private static readonly object Lock = new();

    public static bool Enabled { get; set; }

    public static void Write(string message)
    {
        if (!Enabled) return;

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Lock)
        {
            File.AppendAllText(Path, line + Environment.NewLine);
        }
    }
}
