using System.Drawing;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Capture for game windows (config <c>gameProfiles</c> with capture "screen"). PrintWindow asks a window to
/// paint itself, which OpenGL/DirectX surfaces answer with a blank or stale frame; the compositor's output is
/// the only reliable source short of Windows.Graphics.Capture. So while the game window is in the foreground
/// its client area (no title bar or borders) is copied from the screen. Image pixels then map 1:1 onto the
/// game's own viewport, which is what in-game x,y coordinates refer to.
/// </summary>
public static class GameCapture
{
    public const string Method = "screen-client";

    /// <summary>
    /// Client-area screen copy of a game window, or null when the window is not a game window, its profile
    /// keeps PrintWindow, or it is minimized / not in front (the caller then uses the ordinary window capture).
    /// </summary>
    public static ScreenCapture.Captured? TryClientArea(ServerConfig config, WindowInfo window)
    {
        var profile = config.MatchGame(window);
        if (profile == null || profile.Capture != "screen") return null;
        if (window.Minimized || !WindowActivation.IsForeground(window)) return null;
        var client = ClientRectOnScreen(window.Hwnd);
        if (client == null) return null;
        var captured = ScreenCapture.Region(client.Value);
        return captured with { Method = GameCapture.Method };
    }

    /// <summary>The window's client rectangle in physical screen pixels, clipped to the desktop; null when empty.</summary>
    public static Rectangle? ClientRectOnScreen(IntPtr hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0) return null;
        var origin = new Native.POINT { X = 0, Y = 0 };
        if (!Native.ClientToScreen(hwnd, ref origin)) return null;
        var client = new Rectangle(origin.X, origin.Y, rect.Width, rect.Height);
        client.Intersect(Monitors.VirtualScreen());
        return client.Width > 0 && client.Height > 0 ? client : null;
    }
}
