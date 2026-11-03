using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace IntelliTypeElite;

// Decides whether the keyboard is currently being routed to the Mac by ShareMouse.
//
// ShareMouse gives this PC no direct signal for that, but when the mouse crosses onto
// the Mac the Windows cursor stops dead at the edge of the PC screen it left through
// (observed: pinned at x = width-1 with a frozen y while raw mouse movement keeps
// arriving). So "Mac is active" = the cursor is parked on a PC-screen edge stretch that
// borders a macOS monitor in the user's absolute layout (config "layout"), and
// ShareMouse is running. Checked on demand at button-press time -- no polling.
internal sealed class MacModeDetector
{
    private enum Side { Left, Right, Top, Bottom }

    // A stretch of one PC-screen edge, as 0..1 fractions along that edge, that borders
    // a macOS monitor. Fractions (not pixels) keep this independent of whether the
    // layout's declared resolution matches the real one or of DPI scaling.
    private readonly record struct ExitSegment(Side Side, double Start, double End);

    private readonly record struct Rect(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    // Layout units: two edges closer than this count as touching.
    private const double TouchTolerance = 0.5;

    // Slack on the along-the-edge test so a cursor pinned exactly at a segment boundary
    // still matches despite rounding.
    private const double SegmentSlack = 0.002;

    private readonly List<ExitSegment> _segments = new();

    public MacModeDetector(LayoutConfig? layout)
    {
        if (layout is null) return;

        try
        {
            Build(layout);
        }
        catch (Exception ex)
        {
            // Bad layout = Mac mode stays off; never throw from here (it's built lazily on
            // a HID reader thread, where an exception would kill the reader).
            _segments.Clear();
            Logger.Write($"Ignoring invalid layout (Mac mode disabled): {ex.Message}");
        }
    }

    public bool IsMacActive()
    {
        try
        {
            return CheckMacActive();
        }
        catch (Exception ex)
        {
            // Called from HID reader threads, where a throw would end the thread for the
            // rest of the session. Failing closed (PC mode) is the safe direction.
            Logger.Write($"Mac mode check failed (treating as PC): {ex.Message}");
            return false;
        }
    }

    private bool CheckMacActive()
    {
        if (_segments.Count == 0) return false;
        if (!GetCursorPos(out var cursor)) return false;
        if (Screen.PrimaryScreen is not { } screen) return false;

        var bounds = screen.Bounds;
        bool onExit = false;

        foreach (var segment in _segments)
        {
            bool atEdge;
            double along; // 0..1 position of the cursor along the edge
            switch (segment.Side)
            {
                case Side.Right:
                    atEdge = cursor.X >= bounds.Right - 2;
                    along = (cursor.Y - bounds.Top + 0.5) / bounds.Height;
                    break;
                case Side.Left:
                    atEdge = cursor.X <= bounds.Left + 1;
                    along = (cursor.Y - bounds.Top + 0.5) / bounds.Height;
                    break;
                case Side.Top:
                    atEdge = cursor.Y <= bounds.Top + 1;
                    along = (cursor.X - bounds.Left + 0.5) / bounds.Width;
                    break;
                default: // Bottom
                    atEdge = cursor.Y >= bounds.Bottom - 2;
                    along = (cursor.X - bounds.Left + 0.5) / bounds.Width;
                    break;
            }

            if (atEdge && along >= segment.Start - SegmentSlack && along <= segment.End + SegmentSlack)
            {
                onExit = true;
                break;
            }
        }

        // Checked last: it's the (relatively) expensive part, and the cursor test above
        // rules out nearly every press.
        return onExit && IsShareMouseConnected();
    }

    // ShareMouse's peer-to-peer TCP port (seen on both ends of its one connection).
    private const int ShareMousePort = 6555;

    // How long a "is ShareMouse connected" answer is reused. Enumerating processes and
    // TCP connections is slow enough (tens of ms) to matter on a press that runs under
    // the HID listener's lock.
    private const long ConnectedCacheMs = 2000;

    private static readonly object CacheLock = new();
    private static long _connectedCheckedAt;
    private static bool _connectedHasValue;
    private static bool _connectedValue;

    // Only an edge-parked cursor isn't proof the Mac has the keyboard: with the Mac
    // asleep or offline the cursor can rest on the same edge while the keyboard is still
    // the PC's. Requiring a live ShareMouse connection rules that out. (It can't tell
    // "connected" from "control has crossed over", so the cursor test is still needed.)
    private static bool IsShareMouseConnected()
    {
        lock (CacheLock)
        {
            long now = Environment.TickCount64;
            if (_connectedHasValue && now - _connectedCheckedAt < ConnectedCacheMs)
                return _connectedValue;

            bool connected = IsShareMouseRunning() && HasShareMouseConnection();
            _connectedValue = connected;
            _connectedCheckedAt = now;
            _connectedHasValue = true;
            return connected;
        }
    }

    private static bool IsShareMouseRunning()
    {
        var processes = Process.GetProcessesByName("ShareMouse");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static bool HasShareMouseConnection()
    {
        foreach (var connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            if (connection.State == TcpState.Established
                && (connection.LocalEndPoint.Port == ShareMousePort || connection.RemoteEndPoint.Port == ShareMousePort))
            {
                return true;
            }
        }

        return false;
    }

    private void Build(LayoutConfig layout)
    {
        var rows = layout.Monitors;

        // "N%" spacers are relative to the first real monitor in the first row.
        double baseWidth = 0;
        if (rows.Count > 0)
        {
            foreach (var item in rows[0])
            {
                if (TryParseSpacer(item, out _)) continue;
                baseWidth = ParseMonitor(item).Width;
                break;
            }
        }

        Rect? pc = null;
        var macs = new List<Rect>();
        double y = 0;

        foreach (var row in rows)
        {
            double x = 0;
            double rowHeight = 0;

            foreach (var item in row)
            {
                if (TryParseSpacer(item, out double percent))
                {
                    if (baseWidth <= 0)
                        throw new FormatException("'none:N%' needs a monitor in the first row to measure against");
                    x += baseWidth * percent / 100;
                    continue;
                }

                var (machine, width, height) = ParseMonitor(item);
                var rect = new Rect(x, y, width, height);

                switch (machine)
                {
                    case "windows":
                        if (pc is not null)
                            throw new FormatException("only one 'windows' monitor is supported");
                        pc = rect;
                        break;
                    case "macos":
                        macs.Add(rect);
                        break;
                    default:
                        throw new FormatException($"unknown machine '{machine}' (expected windows, macos or none)");
                }

                x += width;
                rowHeight = Math.Max(rowHeight, height);
            }

            y += rowHeight;
        }

        if (pc is not { } screen)
            throw new FormatException("layout needs a 'windows' monitor");

        foreach (var mac in macs)
        {
            if (Touching(mac.X, screen.Right))
                AddOverlap(Side.Right, mac.Y, mac.Bottom, screen.Y, screen.H);
            if (Touching(mac.Right, screen.X))
                AddOverlap(Side.Left, mac.Y, mac.Bottom, screen.Y, screen.H);
            if (Touching(mac.Y, screen.Bottom))
                AddOverlap(Side.Bottom, mac.X, mac.Right, screen.X, screen.W);
            if (Touching(mac.Bottom, screen.Y))
                AddOverlap(Side.Top, mac.X, mac.Right, screen.X, screen.W);
        }
    }

    private static bool Touching(double a, double b) => Math.Abs(a - b) <= TouchTolerance;

    // Records the part of the PC edge [pcStart, pcStart + pcLength] that the Mac monitor's
    // matching edge [macStart, macEnd] covers, if any. A corner-only touch has no
    // overlap and adds nothing.
    private void AddOverlap(Side side, double macStart, double macEnd, double pcStart, double pcLength)
    {
        double low = Math.Max(macStart, pcStart);
        double high = Math.Min(macEnd, pcStart + pcLength);
        if (high - low <= TouchTolerance) return;

        _segments.Add(new ExitSegment(side, (low - pcStart) / pcLength, (high - pcStart) / pcLength));
    }

    // "machine:WxH" -- the separator may be x, X or *.
    private static (string Machine, double Width, double Height) ParseMonitor(string item)
    {
        int colon = item.IndexOf(':');
        if (colon <= 0)
            throw new FormatException($"'{item}' is not 'machine:WxH'");

        string machine = item[..colon].Trim().ToLowerInvariant();
        string[] size = item[(colon + 1)..].Split(new[] { 'x', 'X', '*' }, StringSplitOptions.TrimEntries);

        if (size.Length != 2
            || !double.TryParse(size[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            || !double.TryParse(size[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            || width <= 0 || height <= 0)
        {
            throw new FormatException($"'{item}' is not 'machine:WxH'");
        }

        return (machine, width, height);
    }

    // "none:N%" -- an inline empty gap. Returns false for anything that isn't a "none"
    // item (so the caller treats it as a monitor), and throws if it is one but malformed.
    private static bool TryParseSpacer(string item, out double percent)
    {
        percent = 0;
        int colon = item.IndexOf(':');
        if (colon <= 0 || !item[..colon].Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;

        string value = item[(colon + 1)..].Trim();
        if (!value.EndsWith('%')
            || !double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out percent)
            || percent < 0)
        {
            throw new FormatException($"'{item}' is not 'none:N%'");
        }

        return true;
    }
}
