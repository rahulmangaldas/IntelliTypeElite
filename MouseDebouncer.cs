using System.Runtime.InteropServices;

namespace IntelliTypeElite;

// Suppresses spurious repeat clicks from a worn mouse switch, system-wide, via a
// low-level mouse hook (WH_MOUSE_LL) -- the only Win32 mechanism that can actually
// block a click from reaching other apps (RawInput, used by ../support/HidDiscovery,
// is a read-only sink and can't do this). Config comes from config.json's
// "mouseDebounce" section and is hot-reloaded by ConfigWatcher via UpdateConfig.
internal sealed class MouseDebouncer : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const uint LLMHF_INJECTED = 0x1;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Rooted as a field, not passed inline as a lambda -- if this delegate isn't kept
    // alive, the GC can collect it while unmanaged code still holds a pointer to it,
    // causing an intermittent CallbackOnCollectedDelegate crash.
    private readonly LowLevelMouseProc _proc;
    private nint _hookHandle;
    private Thread? _hookThread;
    private uint _hookThreadId;

    private sealed class ButtonState
    {
        public uint LastUpTicks;
        public bool SwallowNextUp;
    }

    // Left, Right, Middle, X1, X2.
    private readonly ButtonState[] _state = { new(), new(), new(), new(), new() };

    // Swapped atomically on hot reload; the hook callback always reads a fully-formed
    // snapshot. Per-button state above is intentionally NOT reset on reload, so an
    // in-flight SwallowNextUp is still honored even if the config changes mid-bounce.
    private volatile MouseDebounceConfig _config;

    public MouseDebouncer(MouseDebounceConfig config)
    {
        _config = config;
        _proc = HookCallback;
    }

    public void Start()
    {
        _hookThread = new Thread(RunHookThread) { IsBackground = true, Name = "MouseDebounceHook" };
        _hookThread.Start();
    }

    public void UpdateConfig(MouseDebounceConfig config) => _config = config;

    // Read by Program.cs's tray menu to show current debounce status; the menu only
    // reads this right before it's shown, so no need to notify on change.
    public MouseDebounceConfig CurrentConfig => _config;

    private void RunHookThread()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hookHandle == 0)
        {
            Logger.Write($"MouseDebouncer: hook install failed, error {Marshal.GetLastWin32Error()}");
            return;
        }

        while (GetMessage(out var msg, 0, 0, 0) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var config = _config;
            if (!config.Enabled) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int msg = (int)wParam;
            var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Real mouse clicks only -- this app's own SendInput traffic (wheel scroll,
            // keystrokes) and any other automation is marked injected and left alone.
            if ((hs.flags & LLMHF_INJECTED) != 0)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int buttonIndex = msg switch
            {
                WM_LBUTTONDOWN or WM_LBUTTONUP => 0,
                WM_RBUTTONDOWN or WM_RBUTTONUP => 1,
                WM_MBUTTONDOWN or WM_MBUTTONUP => 2,
                // X-button identity is packed in the HIGH WORD of mouseData, not in
                // wParam -- 1 = XBUTTON1, 2 = XBUTTON2.
                WM_XBUTTONDOWN or WM_XBUTTONUP => ((hs.mouseData >> 16) & 0xFFFF) == 1 ? 3 : 4,
                _ => -1,
            };
            if (buttonIndex < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var btnCfg = config.ButtonByIndex(buttonIndex);
            var state = _state[buttonIndex];
            if (btnCfg is not { Enabled: true }) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            bool isDown = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

            if (!isDown)
            {
                if (state.SwallowNextUp)
                {
                    state.SwallowNextUp = false;
                    LogSuppressed(buttonIndex, "up");
                    return 1;
                }
                state.LastUpTicks = hs.time;
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // Unsigned subtraction wraps correctly even across GetTickCount() rollover.
            uint elapsed = unchecked(hs.time - state.LastUpTicks);
            if (state.LastUpTicks != 0 && elapsed < (uint)btnCfg.Threshold)
            {
                state.SwallowNextUp = true;
                LogSuppressed(buttonIndex, "down");
                return 1;
            }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            // A hook proc must never throw back into the hook chain.
            try { Logger.Write($"MouseDebouncer hook callback error: {ex.Message}"); } catch { }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }
    }

    private static void LogSuppressed(int buttonIndex, string phase) =>
        // Off the hook thread -- Logger.Write does file I/O under a lock, and the
        // callback must stay fast or Windows silently detaches the hook.
        ThreadPool.QueueUserWorkItem(_ => Logger.Write($"MouseDebouncer: suppressed bounced {phase} for button {buttonIndex}"));

    public void Dispose()
    {
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join(TimeSpan.FromSeconds(2));
        if (_hookHandle != 0)
            UnhookWindowsHookEx(_hookHandle);
    }
}
