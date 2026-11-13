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
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // 1 while a Sequence is running; a second press is ignored until it finishes.
    private int _sequenceRunning;

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
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

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

    public void Execute(ButtonConfig button, RootConfig config)
    {
        ActionConfig action;
        Func<bool>? stillMac = null;
        if (config.MacMode.IsMacActive())
        {
            // A Sequence takes a while; this lets it stop if the cursor comes back to
            // the PC mid-run, so the remaining keys don't land in a Windows window.
            stillMac = () => config.MacMode.IsMacActive();

            // The Mac has the keyboard: skip the PC-side resolution entirely (app and
            // modifier overrides describe the Windows foreground app) and only run
            // things that make sense there. Launch/CloseWindow/ShowFavorites would act
            // on this PC, so they're ignored, as is a button with no macAction.
            var macAction = button.MacAction;
            if (macAction is null || macAction.Type is not ("Keystroke" or "Sequence" or "None"))
            {
                Logger.Write($"{button.Name}: Mac mode, no usable macAction -- ignored");
                return;
            }

            Logger.Write($"{button.Name}: Mac mode");
            action = macAction;
        }
        else
        {
            action = ResolveAction(button);
        }

        switch (action.Type)
        {
            case "Sequence":
                RunSequence(button.Name, action, stillMac);
                break;

            case "ShowFavorites":
                ShowFavorites(config);
                Logger.Write($"{button.Name}: showed favorites overlay");
                break;

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

    private static void ShowFavorites(RootConfig config)
    {
        var labels = Enumerable.Range(1, 5)
            .Select(i => config.Buttons.FirstOrDefault(b => b.Name == $"MyFavorites{i}"))
            .Select(b => b is null ? "Unassigned" : DescribeAction(b.Action))
            .ToList();

        UiThread.Context?.Post(_ => new FavoritesOverlay(labels).Show(), null);
    }

    private static string DescribeAction(ActionConfig action) => action.Type switch
    {
        "Launch" when !string.IsNullOrWhiteSpace(action.Path) => DescribeLaunchTarget(action.Path),
        "Keystroke" when !string.IsNullOrWhiteSpace(action.Keys) => DescribeKeystroke(action.Keys),
        _ => "Unassigned",
    };

    private static string DescribeLaunchTarget(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.PathAndQuery is "/" or "" ? uri.Host : $"{uri.Host}/...";

        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                return description;
        }
        catch
        {
            // Not a real exe on disk (or no version info) -- fall through to the filename.
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static string DescribeKeystroke(string combo) =>
        string.Join('+', combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

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
            "win" or "windows" => IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN),
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
            string name = process.ProcessName;

            // Packaged (UWP-style) apps such as Media Player, Calculator and Settings show
            // their window inside a frame owned by ApplicationFrameHost; the real app
            // lives in a child window belonging to another process. Report that process,
            // so appOverrides can match "microsoft.media.player" and not the shared host.
            if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint hostPid = pid;
                uint realPid = 0;
                EnumChildWindows(hWnd, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out uint childPid);
                    if (childPid == 0 || childPid == hostPid) return true;
                    realPid = childPid;
                    return false;
                }, 0);

                if (realPid != 0)
                {
                    using var real = Process.GetProcessById((int)realPid);
                    return real.ProcessName;
                }
            }

            return name;
        }
        catch
        {
            return null;
        }
    }

    private delegate bool EnumChildProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);

    // Accepts combos like "ctrl+alt+t" or a single key like "F5".
    private static void SendKeystroke(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = parts.Select(ToVirtualKey).ToList();
        if (vks.Count == 0) return;

        // A token that isn't a known key (typo, unsupported key) used to be dropped and the
        // rest sent anyway -- "cmd+spcae" became a bare Win tap. Send nothing instead.
        if (vks.Contains(0))
        {
            Logger.Write($"Unknown key in '{combo}' -- nothing sent");
            return;
        }

        // Navigation keys must carry the extended-key flag, otherwise Windows treats them
        // as their numpad twins (Left -> Numpad4, etc.).
        var downs = vks.Select(vk => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0 } }
        });
        var ups = ((IEnumerable<ushort>)vks).Reverse().Select(vk => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0) } }
        });

        var sequence = downs.Concat(ups).ToArray();
        SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<INPUT>());
    }

    // PageUp/PageDown/End/Home/Left/Up/Right/Down (0x21-0x28), Insert, Delete.
    private static bool IsExtendedKey(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    // Runs a "Sequence" action's steps on a background task, so the HID reader thread
    // (which calls Execute while holding a lock) isn't blocked by the delays. Only one
    // sequence runs at a time; a press while one is running is ignored.
    //
    // stillValid (Mac mode only) is re-checked before every step and typed character; the
    // sequence is abandoned the first time it returns false.
    private void RunSequence(string buttonName, ActionConfig action, Func<bool>? stillValid)
    {
        if (action.Steps is not { Count: > 0 })
        {
            Logger.Write($"{buttonName}: sequence has no steps");
            return;
        }

        if (Interlocked.CompareExchange(ref _sequenceRunning, 1, 0) != 0)
        {
            Logger.Write($"{buttonName}: a sequence is already running -- ignored");
            return;
        }

        // Snapshot, so a config hot reload can't change the steps mid-run.
        var steps = action.Steps.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var step in steps)
                {
                    if (!StillValid(buttonName, stillValid)) return;

                    if (!string.IsNullOrWhiteSpace(step.Keys))
                        SendKeystroke(step.Keys);

                    if (!string.IsNullOrEmpty(step.Text))
                    {
                        foreach (char c in step.Text)
                        {
                            if (!StillValid(buttonName, stillValid)) return;
                            SendKeystroke(c == ' ' ? "space" : c.ToString());
                        }
                    }

                    if (step.DelayMs is > 0)
                        await Task.Delay(step.DelayMs.Value);
                }

                Logger.Write($"{buttonName}: sequence finished ({steps.Count} steps)");
            }
            catch (Exception ex)
            {
                Logger.Write($"{buttonName}: sequence failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _sequenceRunning, 0);
            }
        });
    }

    private static bool StillValid(string buttonName, Func<bool>? stillValid)
    {
        if (stillValid is null || stillValid()) return true;

        Logger.Write($"{buttonName}: sequence abandoned (the Mac is no longer active)");
        return false;
    }

    private static ushort ToVirtualKey(string key)
    {
        string k = key.ToLowerInvariant();
        return k switch
        {
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "shift" => 0x10,
            // "cmd" = the Windows key: ShareMouse maps Win to Cmd on the Mac.
            "win" or "windows" or "cmd" or "command" => 0x5B,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "[" => 0xDB,
            "]" => 0xDD,
            // ASCII only: char.IsLetterOrDigit would also accept e.g. 'é', whose upper-case
            // code point is not a meaningful virtual-key code.
            _ when k.Length == 1 && k[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9') => (ushort)char.ToUpperInvariant(k[0]),
            _ when k.StartsWith('f') && int.TryParse(k.AsSpan(1), out int fn) && fn is >= 1 and <= 24 => (ushort)(0x70 + fn - 1),
            _ => 0,
        };
    }
}
