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
    // "None" (log only), "Keystroke" (SendInput), "Launch" (Process.Start).
    [JsonPropertyName("type")] public string Type { get; set; } = "None";
    [JsonPropertyName("keys")] public string? Keys { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
}

internal sealed class ModifierOverride
{
    // "shift", "ctrl", "alt", or a combo like "ctrl+shift". Matched against the live
    // modifier state (GetAsyncKeyState) at the moment the button is pressed.
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

    // Checked when no appOverride matches the foreground app.
    [JsonPropertyName("modifierOverrides")] public List<ModifierOverride>? ModifierOverrides { get; set; }

    // Keyed by process name (with or without ".exe", case-insensitive), e.g. "OUTLOOK".
    // Each entry has its own default action plus its own optional modifierOverrides.
    // See ActionRunner.ResolveAction.
    [JsonPropertyName("appOverrides")] public Dictionary<string, AppOverride>? AppOverrides { get; set; }

    public byte BitMaskValue => Convert.ToByte(
        BitMask.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? BitMask[2..] : BitMask, 16);
}

internal sealed class RootConfig
{
    [JsonPropertyName("device")] public DeviceConfig Device { get; set; } = new();
    [JsonPropertyName("wheel")] public WheelConfig Wheel { get; set; } = new();
    [JsonPropertyName("buttons")] public List<ButtonConfig> Buttons { get; set; } = new();

    public static RootConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RootConfig>(json)
               ?? throw new InvalidDataException($"Could not parse {path}");
    }
}
