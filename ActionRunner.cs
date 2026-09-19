using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IntelliTypeElite;

internal sealed class ActionRunner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    private const uint WM_SYSCOMMAND = 0x0112;
    private const nint SC_CLOSE = 0xF060;

    public void ScrollWheel(int amount)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { mouseData = amount, dwFlags = MOUSEEVENTF_WHEEL } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void Execute(ButtonConfig button)
    {
        var action = ResolveAction(button);
        switch (action.Type)
        {
            case "Keystroke":
                if (!string.IsNullOrWhiteSpace(action.Keys))
                    SendKeystroke(action.Keys);
                Logger.Write($"{button.Name}: sent keystroke '{action.Keys}'");
                break;

            case "CloseWindow":
                // Posts the actual WM_SYSCOMMAND/SC_CLOSE that Alt+F4 triggers
                // internally, rather than simulating the keypress -- some apps
                // (e.g. Windows' packaged Calculator) don't reliably react to a
                // synthetic Alt+F4 via SendInput.
                nint hWnd = GetForegroundWindow();
                if (hWnd != 0)
                    PostMessage(hWnd, WM_SYSCOMMAND, SC_CLOSE, 0);
                Logger.Write($"{button.Name}: closed foreground window");
                break;

            case "Launch":
                if (!string.IsNullOrWhiteSpace(action.Path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(action.Path)
                        {
                            Arguments = action.Args ?? "",
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Write($"{button.Name}: launch failed: {ex.Message}");
                    }
                }
                Logger.Write($"{button.Name}: launched '{action.Path}'");
                break;

            case "None":
            default:
                Logger.Write($"{button.Name}: pressed (no action configured)");
                break;
        }
    }

    // Resolution order: app override (matched by foreground process) takes priority
    // over the button's plain default; within whichever of those applies, a held
    // modifier combo takes priority over that scope's own default action.
    private static ActionConfig ResolveAction(ButtonConfig button)
    {
        string? processName = GetForegroundProcessName();
        AppOverride? matchedApp = FindAppOverride(button, processName);

        if (matchedApp is not null)
        {
            var modifierAction = FindModifierOverride(matchedApp.ModifierOverrides);
            return modifierAction ?? matchedApp.Action;
        }

        return FindModifierOverride(button.ModifierOverrides) ?? button.Action;
    }

    private static AppOverride? FindAppOverride(ButtonConfig button, string? processName)
    {
        if (button.AppOverrides is not { Count: > 0 } || processName is null)
            return null;

        foreach (var (app, appOverride) in button.AppOverrides)
        {
            string normalized = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
            if (string.Equals(normalized, processName, StringComparison.OrdinalIgnoreCase))
                return appOverride;
        }

        return null;
    }

    private static ActionConfig? FindModifierOverride(List<ModifierOverride>? overrides)
    {
        if (overrides is not { Count: > 0 })
            return null;

        // Prefer the entry requiring the most modifiers, so "ctrl+shift" wins over a
        // plain "shift" entry when both are held.
        return overrides
            .Where(o => IsModifierComboActive(o.Modifiers))
            .OrderByDescending(o => o.Modifiers.Count(c => c == '+') + 1)
            .Select(o => o.Action)
            .FirstOrDefault();
    }

    private static bool IsModifierComboActive(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        return parts.All(p => p.ToLowerInvariant() switch
        {
            "shift" => IsKeyDown(VK_SHIFT),
            "ctrl" or "control" => IsKeyDown(VK_CONTROL),
            "alt" => IsKeyDown(VK_MENU),
            _ => false,
        });
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private static string? GetForegroundProcessName()
    {
        try
        {
            nint hWnd = GetForegroundWindow();
            if (hWnd == 0) return null;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    // Accepts combos like "ctrl+alt+t" or a single key like "F5".
    private static void SendKeystroke(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = parts.Select(ToVirtualKey).Where(vk => vk != 0).ToList();
        if (vks.Count == 0) return;

        var downs = vks.Select(vk => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } }
        });
        var ups = ((IEnumerable<ushort>)vks).Reverse().Select(vk => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
        });

        var sequence = downs.Concat(ups).ToArray();
        SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<INPUT>());
    }

    private static ushort ToVirtualKey(string key) => key.ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11,
        "alt" => 0x12,
        "shift" => 0x10,
        "win" or "windows" => 0x5B,
        "esc" or "escape" => 0x1B,
        "tab" => 0x09,
        "enter" or "return" => 0x0D,
        "space" => 0x20,
        _ when key.Length == 1 && char.IsLetterOrDigit(key[0]) => (ushort)char.ToUpperInvariant(key[0]),
        _ when key.StartsWith('f') && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 24 => (ushort)(0x70 + fn - 1),
        _ => 0,
    };
}
