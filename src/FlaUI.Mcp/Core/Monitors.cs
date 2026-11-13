using System.Drawing;
using System.Runtime.InteropServices;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>A monitor in physical pixels. Ids are 1-based: primary = 1, the rest ordered top-to-bottom, left-to-right.</summary>
public sealed record MonitorInfo(int Id, IntPtr Handle, Rectangle Bounds, Rectangle WorkArea, uint Dpi, bool Primary, string Device)
{
    public double Scale => Dpi / 96.0;

    public object ToJson() => new
    {
        id = Id,
        bounds = new { x = Bounds.X, y = Bounds.Y, width = Bounds.Width, height = Bounds.Height },
        dpi = Dpi,
        scale = Math.Round(Scale, 2),
        primary = Primary,
        device = Device
    };
}

public static class Monitors
{
    /// <summary>Enumerates monitors fresh on every call (the layout can change at any time).</summary>
    public static IReadOnlyList<MonitorInfo> All()
    {
        var raw = new List<(IntPtr handle, Native.MONITORINFOEX info, uint dpi)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfoW(hMonitor, ref info))
            {
                uint dpi = 96;
                if (Native.GetDpiForMonitor(hMonitor, Native.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                {
                    dpi = dx;
                }
                raw.Add((hMonitor, info, dpi));
            }
            return true;
        }, IntPtr.Zero);

        var ordered = raw
            .OrderByDescending(m => (m.info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.info.rcMonitor.Top)
            .ThenBy(m => m.info.rcMonitor.Left)
            .ToList();

        var result = new List<MonitorInfo>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (handle, info, dpi) = ordered[i];
            result.Add(new MonitorInfo(
                i + 1,
                handle,
                info.rcMonitor.ToRectangle(),
                info.rcWork.ToRectangle(),
                dpi,
                (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                info.szDevice));
        }
        return result;
    }

    public static MonitorInfo? ById(int id) => All().FirstOrDefault(m => m.Id == id);

    public static MonitorInfo? Primary() => All().FirstOrDefault(m => m.Primary) ?? All().FirstOrDefault();

    /// <summary>Monitor with the largest overlap with the rectangle; nearest monitor when there is no overlap.</summary>
    public static MonitorInfo? For(Rectangle rect)
    {
        var all = All();
        if (all.Count == 0) return null;
        MonitorInfo? best = null;
        long bestArea = -1;
        foreach (var m in all)
        {
            var inter = Rectangle.Intersect(m.Bounds, rect);
            long area = inter.IsEmpty ? 0 : (long)inter.Width * inter.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = m;
            }
        }
        if (bestArea > 0) return best;

        var r = Native.RECT.From(rect);
        var h = Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == h) ?? best;
    }

    public static MonitorInfo? For(Point point)
    {
        var all = All();
        var h = Native.MonitorFromPoint(new Native.POINT { X = point.X, Y = point.Y }, Native.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == h) ?? all.FirstOrDefault(m => m.Bounds.Contains(point));
    }

    public static MonitorInfo? ForWindow(IntPtr hwnd)
    {
        var all = All();
        var h = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == h);
    }

    /// <summary>Bounding box of all monitors (virtual screen) in physical pixels.</summary>
    public static Rectangle VirtualScreen()
    {
        var all = All();
        if (all.Count == 0) return Rectangle.Empty;
        var r = all[0].Bounds;
        foreach (var m in all.Skip(1))
        {
            r = Rectangle.Union(r, m.Bounds);
        }
        return r;
    }
}
