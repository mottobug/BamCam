// Squircle as a script of the Scripts menu: the same shapes as the Squircle plugin (Draw > Squircle).
// Idea, formulas and parameters: GeoffreyGRoy, author of the Squircle Pro plugin for CamBam.
// Original: http://www.atelier-des-fougeres.fr/Cambam/Aide/Plugins/SquirclePlugin.html
// Written for BamCam from the documentation of that plugin; it contains no code of it.
//
// The difference to the plugin: the plugin makes an object whose values stay in the properties. This script asks for
// the values and draws a plain polyline. To get another shape, run it again.

using System.Threading.Tasks;

const int ManyPoints = 4000, MostPoints = 20000; // from here on a note, above the limit it is cut down
const int MeasureSamples = 3600;                 // samples for measuring a shape: its extents and its length
const double Smallest = 1e-3;

// Several numbers in one dialog, separated by spaces. Null when cancelled or when they are not such numbers.
async Task<double[]?> AskValues(string label, string values)
{
    if (await AskText("Squircle", label, values) is not { } text) return null;
    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var numbers = new double[parts.Length];
    var valid = parts.Length == values.Split(' ').Length;
    for (var i = 0; i < parts.Length; i++)
        valid &= double.TryParse(parts[i].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]);
    if (valid) return numbers;
    Log($"Squircle: expected numbers for \"{label}\", got \"{text}\".");
    return null;
}

// ---------------------------------------------------------------- the values

if (await AskText("Squircle", "Shape type (Simple or Complex):", "Simple") is not { } shapeType) return;
var complex = shapeType.Trim().Equals("Complex", StringComparison.OrdinalIgnoreCase);
if (!complex && !shapeType.Trim().Equals("Simple", StringComparison.OrdinalIgnoreCase))
{
    Log($"Squircle: unknown shape type \"{shapeType}\".");
    return;
}

// Simple: 2 = circle or ellipse, above 2 = rounded rectangle, 1 to 2 = rounded diamond, below 1 = concave diamond.
// Complex: m sets the basic outline (3 sided, 4 sided and so on), the others vary it.
double m = 4, n1 = 4, n2 = 4, n3 = 2, a = 1, b = 1.2;
if (complex)
{
    if (await AskValues("m  n1  n2  n3  a  b:", "4 4 4 2 1 1.2") is not { } values) return;
    (m, n1, n2, n3, a, b) = (values[0], values[1], values[2], values[3], values[4], values[5]);
}
else
{
    if (await AskNumber("Squircle", "n1 (2 = circle, more = rounded rectangle, less = diamond):", 4) is not { } value) return;
    n1 = value;
}

if (await AskValues("Rad-X  Rad-Y  Orig-X  Orig-Y:", "50 50 0 0") is not { } place) return;
var (rx, ry, origin) = (Math.Max(place[0], Smallest), Math.Max(place[1], Smallest), P(place[2], place[3]));

if (await AskText("Squircle", "Accuracy (Angle or EdgeLength):", "Angle") is not { } accuracy) return;
var byAngle = accuracy.Trim().Equals("Angle", StringComparison.OrdinalIgnoreCase);
if (!byAngle && !accuracy.Trim().Equals("EdgeLength", StringComparison.OrdinalIgnoreCase))
{
    Log($"Squircle: unknown accuracy \"{accuracy}\".");
    return;
}
var size = byAngle
    ? await AskNumber("Squircle", "Angle of a segment at the centre, in degrees:", 1)
    : await AskNumber("Squircle", "Length of a segment:", 0.87266);
if (size is not { } segment) return;

// ---------------------------------------------------------------- the formulas

// Simple: |x / rx|^n1 + |y / ry|^n1 = 1, at an angle over the whole curve.
Point2 SimplePoint(double t)
{
    var exponent = 2 / Math.Max(n1, 0.05);
    double Curve(double value) => Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);
    return P(rx * Curve(Math.Cos(t)), ry * Curve(Math.Sin(t)));
}

// Complex: r(t) = (|cos(m t / 4)|^n2 / a + |sin(m t / 4)|^n3 / b)^(-n1), before scaling. Null where it has no value.
Point2? ComplexPoint(double t)
{
    var angle = Math.Max(m, Smallest) * t / 4;
    var sum = Math.Pow(Math.Abs(Math.Cos(angle)), n2) / Math.Max(a, Smallest) + Math.Pow(Math.Abs(Math.Sin(angle)), n3) / Math.Max(b, Smallest);
    var r = Math.Pow(sum, -n1);
    return double.IsFinite(r) ? P(r * Math.Cos(t), r * Math.Sin(t)) : null;
}

// A complex shape is scaled to the radii by its largest distances from the origin along X and Y.
double scaleX = 1, scaleY = 1;
if (complex)
{
    double maxX = 0, maxY = 0;
    for (var i = 0; i < MeasureSamples; i++)
        if (ComplexPoint(2 * Math.PI * i / MeasureSamples) is { } p)
        {
            maxX = Math.Max(maxX, Math.Abs(p.X));
            maxY = Math.Max(maxY, Math.Abs(p.Y));
        }
    if (maxX > 0) scaleX = rx / maxX;
    if (maxY > 0) scaleY = ry / maxY;
}

Point2? PointAt(double t)
{
    if (!complex) return SimplePoint(t);
    return ComplexPoint(t) is { } p ? P(p.X * scaleX, p.Y * scaleY) : null;
}

// ---------------------------------------------------------------- the outline

double wanted;
if (byAngle)
{
    wanted = 360 / Math.Max(segment, Smallest);
}
else
{
    var length = 0.0;
    Point2? previous = null, first = null;
    for (var i = 0; i < MeasureSamples; i++)
    {
        if (PointAt(2 * Math.PI * i / MeasureSamples) is not { } p) continue;
        if (previous is { } q) length += Point2.Distance(q, p);
        first ??= p;
        previous = p;
    }
    if (previous is { } last && first is { } start) length += Point2.Distance(last, start);
    wanted = length / Math.Max(segment, Smallest);
}
var requested = (int)Math.Clamp(Math.Ceiling(wanted - 1e-9), 3, int.MaxValue / 2);
var count = Math.Min(requested, MostPoints);
if (requested > MostPoints) Log($"Squircle: {requested} points asked for: limited to {MostPoints}.");
else if (requested > ManyPoints) Log($"Squircle: many points ({requested}): the object gets slow.");

var points = new List<Point2>(count);
for (var i = 0; i < count; i++)
    if (PointAt(2 * Math.PI * i / count) is { } p)
        points.Add(origin + p);
if (points.Count < 3)
{
    Log("Squircle: these values give no shape.");
    return;
}
Add(Path2.FromPoints(points, closed: true));
Log($"Squircle: {points.Count} points.");
