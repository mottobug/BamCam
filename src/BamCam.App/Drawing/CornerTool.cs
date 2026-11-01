using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>
/// Fillet / chamfer: click the corners of one polyline to round them (fillet), cut them (chamfer) or add a corner
/// overcut. Typing changes the value, Space switches the kind, "-" makes the fillet inverted; each corner is one
/// undoable step. Esc ends.
/// </summary>
public sealed class CornerTool(PolylineEntity target) : ViewTool
{
    /// <summary>Last used kind and values (kept for the next use of the tool).</summary>
    public static CornerKind Kind { get; set; } = CornerKind.Fillet;
    public static double FilletRadius { get; set; } = 2;
    public static double ChamferDistance { get; set; } = 1;
    public static double OvercutRadius { get; set; } = 1.5;

    private string _typed = "";
    private Point2 _cursor;

    private double Value
    {
        get => Kind switch { CornerKind.Fillet => FilletRadius, CornerKind.Chamfer => ChamferDistance, _ => OvercutRadius };
        set
        {
            switch (Kind)
            {
                case CornerKind.Fillet: FilletRadius = value; break;
                case CornerKind.Chamfer: ChamferDistance = value; break;
                default: OvercutRadius = value; break;
            }
        }
    }

    public override string Prompt
    {
        get
        {
            var what = Kind switch
            {
                CornerKind.Fillet => FilletRadius < 0 ? T("Inverted fillet, radius") : T("Fillet, radius"),
                CornerKind.Chamfer => T("Chamfer, distance"),
                _ => T("Corner overcut, tool radius"),
            };
            var value = _typed.Length > 0 ? _typed + "_" : Math.Abs(Value).ToString("0.###", Culture);
            return $"{what} {value}:  {T("click corners  [type value, Space = fillet / chamfer / overcut, - = inverted fillet, Esc = end]")}";
        }
    }

    private Path2 WorldPath => target.ToPaths().First();

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        _cursor = world;
        Host.Invalidate();
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        AcceptTyped();
        var path = WorldPath;
        var index = CornerOps.NearestVertex(path, Host.View.ToWorld(screen), Host.View.ToWorldLength(8));
        if (index < 0) return;
        var (result, error) = CornerOps.Apply(path, index, Kind, Kind == CornerKind.Fillet ? FilletRadius : Math.Abs(Value));
        if (result == null)
        {
            Host.SetPrompt($"{T("This corner cannot be changed")} ({T(error ?? "")}).  {Prompt}");
            return;
        }
        Host.Session.Execute(Kind switch { CornerKind.Fillet => T("Fillet"), CornerKind.Chamfer => T("Chamfer"), _ => T("Corner overcut") }, () =>
        {
            // The new path is in drawing coordinates: the polyline loses its transform.
            target.Transform = Matrix4.Identity;
            target.Closed = result.Closed;
            target.Points.Clear();
            target.Points.AddRange(result.Vertices);
        });
        Host.SetPrompt(Prompt);
        Host.Invalidate();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Escape:
                if (_typed.Length > 0) _typed = "";
                else
                {
                    Host.EndTool();
                    return true;
                }
                break;
            case Key.Space:
                AcceptTyped();
                Kind = Kind switch { CornerKind.Fillet => CornerKind.Chamfer, CornerKind.Chamfer => CornerKind.Overcut, _ => CornerKind.Fillet };
                break;
            case Key.OemMinus or Key.Subtract when Kind == CornerKind.Fillet:
                AcceptTyped();
                FilletRadius = -FilletRadius;
                break;
            case >= Key.D0 and <= Key.D9:
                _typed += (char)('0' + (key - Key.D0));
                break;
            case >= Key.NumPad0 and <= Key.NumPad9:
                _typed += (char)('0' + (key - Key.NumPad0));
                break;
            case Key.OemPeriod or Key.OemComma or Key.Decimal when !_typed.Contains('.'):
                _typed += ".";
                break;
            case Key.Back when _typed.Length > 0:
                _typed = _typed[..^1];
                break;
            case Key.Enter or Key.Return:
                AcceptTyped();
                break;
            default:
                return false;
        }
        Host.SetPrompt(Prompt);
        Host.Invalidate();
        return true;
    }

    /// <summary>Takes over a typed value (keeping the sign of an inverted fillet).</summary>
    private void AcceptTyped()
    {
        if (_typed.Length == 0) return;
        if (double.TryParse(_typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            Value = Kind == CornerKind.Fillet && FilletRadius < 0 ? -v : v;
        _typed = "";
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        // Corner markers, the one under the cursor highlighted.
        var path = WorldPath;
        var hot = CornerOps.NearestVertex(path, _cursor, view.ToWorldLength(8));
        var pen = new Pen(Brushes.Yellow, 1);
        var hotPen = new Pen(Brushes.Orange, 2);
        for (var i = 0; i < path.Count; i++)
        {
            if (!path.Closed && (i == 0 || i == path.Count - 1)) continue;
            var s = view.ToScreen(path.Vertices[i].XY);
            dc.DrawEllipse(null, i == hot ? hotPen : pen, s, i == hot ? 6 : 4, i == hot ? 6 : 4);
        }
        // Preview of the change at the corner under the cursor.
        if (hot >= 0 && CornerOps.Apply(path, hot, Kind, Kind == CornerKind.Fillet ? FilletRadius : Math.Abs(Value)).Path is { } preview)
            DrawPath(dc, view, preview, PreviewPen);
    }
}
