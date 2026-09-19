using HidSharp;

namespace IntelliTypeElite;

// Reads the Wireless Desktop Elite keyboard's vendor-specific HID collection
// (not claimed by any Windows class driver, unlike its standard keyboard
// collection) and dispatches button presses / wheel movement per config.json.
// Mapping was reverse-engineered via HidDiscovery against the real device --
// see ../HidDiscovery.
internal sealed class HidListener
{
    // Swapped by UpdateConfig (see ConfigWatcher); reads/writes go through
    // the _buttonState lock so a report is always handled against a config
    // whose button list matches _buttonState's keys.
    private RootConfig _config;
    private readonly ActionRunner _actions = new();
    private readonly Dictionary<string, bool> _buttonState = new();
    private readonly List<Thread> _threads = new();
    private volatile bool _running;

    public HidListener(RootConfig config)
    {
        _config = config;
        foreach (var button in _config.Buttons)
            _buttonState[button.Name] = false;
    }

    // Note: this only swaps button/wheel/action mappings. It does not affect which
    // HID collections are already open -- changing device.vendorId/productId still
    // requires a restart.
    public void UpdateConfig(RootConfig newConfig)
    {
        lock (_buttonState)
        {
            _config = newConfig;
            _buttonState.Clear();
            foreach (var button in newConfig.Buttons)
                _buttonState[button.Name] = false;
        }
        Logger.Write("Config reloaded.");
    }

    // The receiver enumerates as several HID collections sharing the same
    // VID/PID (composite device) -- only one of them actually carries the
    // button/wheel reports, and which one isn't predictable in advance,
    // so every matching collection is opened and read in parallel.
    public void Start()
    {
        var devices = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == _config.Device.VendorIdValue && d.ProductID == _config.Device.ProductIdValue)
            .ToList();

        if (devices.Count == 0)
        {
            Logger.Write($"No HID device found for VID={_config.Device.VendorId} PID={_config.Device.ProductId}. Is the keyboard's receiver plugged in?");
            return;
        }

        _running = true;
        foreach (var device in devices)
        {
            var thread = new Thread(() => Run(device)) { IsBackground = true };
            _threads.Add(thread);
            thread.Start();
        }
    }

    private void Run(HidDevice device)
    {
        if (!device.TryOpen(out var stream) || stream is null)
        {
            Logger.Write($"Could not open {device.DevicePath} (in use elsewhere, or claimed by a class driver).");
            return;
        }

        Logger.Write($"Listening on {device.DevicePath}");
        var buffer = new byte[device.GetMaxInputReportLength()];
        stream.ReadTimeout = Timeout.Infinite;

        using (stream)
        {
            while (_running)
            {
                int count;
                try
                {
                    count = stream.Read(buffer);
                }
                catch (Exception ex)
                {
                    Logger.Write($"Read error: {ex.Message}");
                    return;
                }
                if (count <= 0) continue;

                HandleReport(buffer.AsSpan(0, count));
            }
        }
    }

    // Called concurrently from each collection's reader thread.
    private void HandleReport(ReadOnlySpan<byte> report)
    {
        if (report.Length == 0) return;
        int reportId = report[0];

        RootConfig config;
        lock (_buttonState) { config = _config; }

        if (reportId == config.Wheel.ReportId)
        {
            HandleWheel(report, config);
            return;
        }

        foreach (var button in config.Buttons)
        {
            if (button.ReportId != reportId) continue;
            if (button.ByteOffset >= report.Length) continue;

            bool pressed = (report[button.ByteOffset] & button.BitMaskValue) != 0;

            lock (_buttonState)
            {
                // Config may have been reloaded between the read above and here;
                // fall back to "not pressed" for a button that no longer exists.
                bool wasPressed = _buttonState.TryGetValue(button.Name, out var prev) && prev;
                if (pressed && !wasPressed)
                    _actions.Execute(button);
                _buttonState[button.Name] = pressed;
            }
        }
    }

    private void HandleWheel(ReadOnlySpan<byte> report, RootConfig config)
    {
        int offset = config.Wheel.DeltaByteOffset;
        if (offset >= report.Length) return;

        sbyte rawDelta = unchecked((sbyte)report[offset]);
        if (rawDelta == 0) return;

        _actions.ScrollWheel(rawDelta * config.Wheel.NotchSize);
    }

    public void Stop() => _running = false;
}
