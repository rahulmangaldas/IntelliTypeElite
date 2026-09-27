namespace IntelliTypeElite;

// Borderless, topmost popup shown by ActionRunner.ShowFavorites, listing what each of the
// MyFavorites1-5 buttons is currently assigned to. Must be constructed on the UI thread --
// see UiThread.
internal sealed class FavoritesOverlay : Form
{
    private const int DismissAfterMs = 2500;

    public FavoritesOverlay(IReadOnlyList<string> labels)
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        Padding = new Padding(12);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = labels.Count,
        };

        for (int i = 0; i < labels.Count; i++)
        {
            stack.Controls.Add(new Label
            {
                Text = $"{i + 1}: {labels[i]}",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f),
                AutoSize = true,
                Margin = new Padding(0, i == 0 ? 0 : 4, 0, 0),
            });
        }

        Controls.Add(stack);

        var timer = new System.Windows.Forms.Timer { Interval = DismissAfterMs };
        timer.Tick += (_, _) => Close();
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var workArea = Screen.PrimaryScreen?.WorkingArea ?? Bounds;
        Location = new Point(workArea.Right - Width - 16, workArea.Bottom - Height - 16);
    }
}
