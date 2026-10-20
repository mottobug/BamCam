using Avalonia;
using BamCam.Core.Geometry;

namespace BamCam.App.Drawing;

/// <summary>
/// Orthographic camera: world → rotate (yaw about Z, then pitch about X) → scale → pan.
/// Screen Y grows downward, world Y upward.
/// </summary>
public sealed class ViewTransform
{
    public double Scale { get; set; } = 4;
    public Point Pan { get; set; }

    /// <summary>Rotation about the world Z axis in radians.</summary>
    public double Yaw { get; private set; }

    /// <summary>Tilt about the screen X axis in radians (0 = top view).</summary>
    public double Pitch { get; private set; }

    private double _r00 = 1, _r01, _r02, _r10, _r11 = 1, _r12, _r20, _r21, _r22 = 1;

    public bool IsTopView => Math.Abs(Yaw) < 1e-12 && Math.Abs(Pitch) < 1e-12;

    public void SetRotation(double yaw, double pitch)
    {
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -Math.PI, Math.PI);
        var cy = Math.Cos(Yaw);
        var sy = Math.Sin(Yaw);
        var cp = Math.Cos(Pitch);
        var sp = Math.Sin(Pitch);
        // R = Rx(pitch) * Rz(yaw), applied to column vectors.
        _r00 = cy; _r01 = -sy; _r02 = 0;
        _r10 = cp * sy; _r11 = cp * cy; _r12 = -sp;
        _r20 = sp * sy; _r21 = sp * cy; _r22 = cp;
    }

    public void ResetRotation() => SetRotation(0, 0);

    /// <summary>Camera-space coordinates (x right, y up, z towards viewer).</summary>
    public (double X, double Y, double Z) Rotate(Point3 p) => (
        _r00 * p.X + _r01 * p.Y + _r02 * p.Z,
        _r10 * p.X + _r11 * p.Y + _r12 * p.Z,
        _r20 * p.X + _r21 * p.Y + _r22 * p.Z);

    public Point ToScreen(Point3 p)
    {
        var (x, y, _) = Rotate(p);
        return new Point(x * Scale + Pan.X, -y * Scale + Pan.Y);
    }

    public Point ToScreen(Point2 p) => ToScreen(p.ToPoint3());

    /// <summary>Maps a screen point onto the world plane Z = <paramref name="z"/>.</summary>
    public Point2 ToWorld(Point screen, double z = 0)
    {
        var cx = (screen.X - Pan.X) / Scale;
        var cy = -(screen.Y - Pan.Y) / Scale;
        // world = Rᵀ · (cx, cy, cz); choose cz so that world.Z = z.
        var cz = Math.Abs(_r22) < 1e-9 ? 0 : (z - (_r02 * cx + _r12 * cy)) / _r22;
        var wx = _r00 * cx + _r10 * cy + _r20 * cz;
        var wy = _r01 * cx + _r11 * cy + _r21 * cz;
        return new Point2(wx, wy);
    }

    public double ToScreenLength(double worldLength) => worldLength * Scale;
    public double ToWorldLength(double pixels) => pixels / Scale;

    /// <summary>Zooms by <paramref name="factor"/> keeping the world point under <paramref name="anchor"/> fixed.</summary>
    public void ZoomAt(Point anchor, double factor)
    {
        var newScale = Math.Clamp(Scale * factor, 1e-4, 1e6);
        factor = newScale / Scale;
        Pan = new Point(anchor.X - (anchor.X - Pan.X) * factor, anchor.Y - (anchor.Y - Pan.Y) * factor);
        Scale = newScale;
    }

    /// <summary>Fits a bounding box into a viewport of the given size with a margin.</summary>
    public void Fit(BoundingBox box, Size viewport, double margin = 0.08)
    {
        if (box.IsEmpty || viewport.Width < 1 || viewport.Height < 1) return;
        // Project the 8 corners to camera space to fit rotated views too.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
        {
            var r = Rotate(new Point3(x, y, z));
            minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.X);
            minY = Math.Min(minY, r.Y); maxY = Math.Max(maxY, r.Y);
        }
        var w = Math.Max(maxX - minX, 1e-6);
        var h = Math.Max(maxY - minY, 1e-6);
        var usable = 1 - 2 * margin;
        Scale = Math.Min(viewport.Width * usable / w, viewport.Height * usable / h);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        Pan = new Point(viewport.Width / 2 - cx * Scale, viewport.Height / 2 + cy * Scale);
    }
}
