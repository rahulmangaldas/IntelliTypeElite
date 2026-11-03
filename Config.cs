using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntelliTypeElite;

internal sealed class DeviceConfig
{
    [JsonPropertyName("vendorId")] public string VendorId { get; set; } = "0x0000";
    [JsonPropertyName("productId")] public string ProductId { get; set; } = "0x0000";

    public int VendorIdValue => ParseHex(VendorId);
    public int ProductIdValue => ParseHex(ProductId);

    private static int ParseHex(string s) =>
        Convert.ToInt32(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 16);
}

internal sealed class WheelConfig
{
    [JsonPropertyName("reportId")] public int ReportId { get; set; }
    [JsonPropertyName("deltaByteOffset")] public int DeltaByteOffset { get; set; }
    [JsonPropertyName("notchSize")] public int NotchSize { get; set; } = 120;
}

internal sealed class ActionConfig
{
    // "None" (log only), "Keystroke" (SendInput), "Launch" (Process.Start),
    // "CloseWindow" (WM_SYSCOMMAND/SC_CLOSE), "ShowFavorites" (pops up an overlay listing
    // the MyFavorites1-5 buttons' current assignments), "Sequence" (runs `steps` in order:
    // keystrokes, typed text and pauses -- e.g. launching a Mac app via Spotlight).
    [JsonPropertyName("type")] public string Type { get; set; } = "None";
    [JsonPropertyName("keys")] public string? Keys { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
    [JsonPropertyName("steps")] public List<ActionStep>? Steps { get; set; }
}

// One step of a "Sequence" action. A step may set any of the three; they run in the
// order keys -> text -> delayMs.
internal sealed class ActionStep
{
    // Same combo syntax as a Keystroke action, e.g. "cmd+space".
    [JsonPropertyName("keys")] public string? Keys { get; set; }

    // Typed one key at a time (letters, digits and spaces only; no shift).
    [JsonPropertyName("text")] public string? Text { get; set; }

    // Pause after this step, in milliseconds.
    [JsonPropertyName("delayMs")] public int? DelayMs { get; set; }
}

internal sealed class ModifierOverride
{
    // "shift", "ctrl", "alt", "win", or a combo like "ctrl+shift". Matched against the
    // live modifier state (GetAsyncKeyState) at the moment the button is pressed.
    [JsonPropertyName("modifiers")] public string Modifiers { get; set; } = "";
    [JsonPropertyName("action")] public ActionConfig Action { get; set; } = new();
}

internal sealed class AppOverride
{
    [JsonPropertyName("action")] public ActionConfig Action { get; set; } = new();
    [JsonPropertyName("modifierOverrides")] public List<ModifierOverride>? ModifierOverrides { get; set; }
}

internal sealed class ButtonConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("reportId")] public int ReportId { get; set; }
    [JsonPropertyName("byteOffset")] public int ByteOffset { get; set; }
    [JsonPropertyName("bitMask")] public string BitMask { get; set; } = "0x00";
    [JsonPropertyName("action")] public ActionConfig Action { get; set; } = new();

    // Used instead of `action` (and instead of every override) while the Mac is the
    // active ShareMouse machine -- see MacModeDetector. Only Keystroke, Sequence and None
    // are honored there; a button without one does nothing in Mac mode.
    [JsonPropertyName("macAction")] public ActionConfig? MacAction { get; set; }

    // Checked when no appOverride matches the foreground app.
    [JsonPropertyName("modifierOverrides")] public List<ModifierOverride>? ModifierOverrides { get; set; }

    // Keyed by process name (with or without ".exe", case-insensitive), e.g. "OUTLOOK".
    // Each entry has its own default action plus its own optional modifierOverrides.
    // See ActionRunner.ResolveAction.
    [JsonPropertyName("appOverrides")] public Dictionary<string, AppOverride>? AppOverrides { get; set; }

    public byte BitMaskValue => Convert.ToByte(
        BitMask.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? BitMask[2..] : BitMask, 16);
}

internal sealed class MouseButtonDebounceConfig
{
    // "left", "right", "middle", "x1", or "x2".
    [JsonPropertyName("button")] public string Button { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    // In milliseconds.
    [JsonPropertyName("threshold")] public int Threshold { get; set; } = 30;
}

internal sealed class MouseDebounceConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("buttons")] public List<MouseButtonDebounceConfig> Buttons { get; set; } = new();

    public MouseButtonDebounceConfig? ButtonByIndex(int index)
    {
        if (!Enabled) return null;
        string name = index switch { 0 => "left", 1 => "right", 2 => "middle", 3 => "x1", 4 => "x2", _ => "" };
        return Buttons.Find(b => string.Equals(b.Button, name, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class LayoutConfig
{
    // Absolute monitor layout shared with ShareMouse: rows top to bottom, items left to
    // right. Each item is "machine:WxH" (machine = windows or macos; "x" or "*" both
    // work) or "none:N%", an inline empty gap N% of the first monitor's width in the
    // first row. Example: [["windows:3840x2160", "macos:3840x2160"], ["none:25%", "macos:1920x1080"]]
    [JsonPropertyName("monitors")] public List<List<string>> Monitors { get; set; } = new();
}

internal sealed class RootConfig
{
    [JsonPropertyName("device")] public DeviceConfig Device { get; set; } = new();
    [JsonPropertyName("wheel")] public WheelConfig Wheel { get; set; } = new();
    [JsonPropertyName("buttons")] public List<ButtonConfig> Buttons { get; set; } = new();

    // Optional -- default has Enabled=false, so existing config.json files without
    // this section still deserialize and behave unchanged.
    [JsonPropertyName("mouseDebounce")] public MouseDebounceConfig MouseDebounce { get; set; } = new();

    // Optional -- absent means Mac mode is never detected.
    [JsonPropertyName("layout")] public LayoutConfig? Layout { get; set; }

    // Built lazily and cached for this config instance (a hot reload swaps in a new
    // RootConfig, so it is rebuilt from the new layout). Racing threads at worst build
    // it twice.
    private MacModeDetector? _macMode;
    [JsonIgnore] public MacModeDetector MacMode => _macMode ??= new MacModeDetector(Layout);

    public static RootConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RootConfig>(json)
               ?? throw new InvalidDataException($"Could not parse {path}");
    }
}
