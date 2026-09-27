namespace IntelliTypeElite;

// Lets code running on HidListener's background read threads marshal work (e.g. showing a
// Form) back onto the STA thread Application.Run() drives. Set once from Program.Main right
// after ApplicationConfiguration.Initialize(), which is what creates the WinForms sync context.
internal static class UiThread
{
    public static SynchronizationContext? Context { get; set; }
}
