using Avalonia;
using Avalonia.Controls;

namespace BamCam.App;

/// <summary>
/// Keeps windows inside the work area of their screen. On small or highly scaled displays
/// (e.g. 1280x800 at 150 % = 853x501 units) a window larger than the screen would otherwise be
/// centred with its title bar (minimise, maximise, close) above the top edge.
/// </summary>
public static class WindowFit
{
    /// <summary>Room for the window frame and title bar, in device independent units.</summary>
    private const double FrameWidth = 16, FrameHeight = 48;

    public static void KeepOnScreen(Window w)
    {
        if (w.WindowState != WindowState.Normal) return;
        var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
        if (screen == null) return;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        var maxW = area.Width / scaling - FrameWidth;
        var maxH = area.Height / scaling - FrameHeight;

        var resized = false;
        if (w.MinWidth > maxW) w.MinWidth = Math.Max(0, maxW);
        if (w.MinHeight > maxH) w.MinHeight = Math.Max(0, maxH);
        if (w.Bounds.Width > maxW) { w.Width = maxW; resized = true; }
        if (w.Bounds.Height > maxH) { w.Height = maxH; resized = true; }

        var outerW = (int)((Math.Min(w.Bounds.Width, maxW) + FrameWidth) * scaling);
        var outerH = (int)((Math.Min(w.Bounds.Height, maxH) + FrameHeight) * scaling);
        var x = w.Position.X;
        var y = w.Position.Y;
        if (resized)
        {
            x = area.X + (area.Width - outerW) / 2;
            y = area.Y + (area.Height - outerH) / 2;
        }
        // The title bar must stay reachable.
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - outerW));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - outerH));
        if (x != w.Position.X || y != w.Position.Y) w.Position = new PixelPoint(x, y);
    }
}