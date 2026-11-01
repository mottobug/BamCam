using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using BamCam.Core.Geometry;

namespace BamCam.Core.Model;

/// <summary>
/// Script object: a drawing object whose outlines come from a C# script stored in the drawing (BamCam's counterpart
/// of CamBam's script objects; CamBam's Python scripts do not run). The script runs only when the user allowed it
/// (a drawing from elsewhere could otherwise run any code); until then the outlines saved in the drawing are shown.
/// </summary>
public sealed class ScriptEntity : ParametricEntity
{
    public const string Type = "bamcam.script";

    public override string ParametricType => Type;
    public override string TypeName => "Script";

    /// <summary>The C# script; it adds outlines with Add(...) (see <see cref="ScriptContext"/>).</summary>
    [Category("Script"), Description("C# script that adds the outlines (Edit > Edit Script). Runs only after you allowed it.")]
    public string Script { get; set; } = DefaultScript;

    /// <summary>Why the script gave no outlines (not allowed, compile or run error), or null.</summary>
    [Category("Script"), DisplayName("Script Status")]
    public string Status
    {
        get
        {
            var computed = IsComputed; // runs the script if needed, which sets the status
            return _status ?? (computed ? "OK" : "");
        }
    }

    private string? _status;

    /// <summary>Compiles and runs a script (set by the application): outlines, or null and an error message.</summary>
    public static Func<string, (IReadOnlyList<Path2>? Paths, string? Error)>? Runner { get; set; }

    /// <summary>Whether a script (by <see cref="Hash"/>) may run (set by the application).</summary>
    public static Func<string, bool> IsAllowed { get; set; } = _ => false;

    /// <summary>Identity of a script text for the user's approval.</summary>
    public static string Hash(string script) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    protected override IEnumerable<Path2>? Generate()
    {
        if (Runner == null)
        {
            _status = Localization.Tr.T("Scripts are not available.");
            return null;
        }
        if (!IsAllowed(Hash(Script)))
        {
            _status = Localization.Tr.T("Not allowed to run: showing the saved outlines.");
            return null;
        }
        var (paths, error) = Runner(Script);
        _status = error;
        return paths;
    }

    public const string DefaultScript =
        """
        // C# script: add outlines with Add(...). Lengths in drawing units, angles in degrees.
        // Helpers: Circle(x, y, r), Rect(x, y, w, h), Line(x1, y1, x2, y2), Arc(x, y, r, start, sweep),
        //          Polyline(closed, x1, y1, x2, y2, ...), Text is not available.
        var holes = 8;
        var pitchRadius = 30;
        Add(Circle(0, 0, 40));
        for (var i = 0; i < holes; i++)
        {
            var a = i * 2 * Math.PI / holes;
            Add(Circle(pitchRadius * Math.Cos(a), pitchRadius * Math.Sin(a), 3));
        }
        """;
}

/// <summary>What a script object's script can use: Add(...) and helpers that build outlines.</summary>
public class ScriptContext
{
    /// <summary>The outlines added so far.</summary>
    public List<Path2> Paths { get; } = [];

    public void Add(Path2 path) => Paths.Add(path);

    public void Add(IEnumerable<Path2> paths) => Paths.AddRange(paths);

    public static Point2 P(double x, double y) => new(x, y);

    public static Path2 Circle(double x, double y, double radius) => new(
        [new Vertex(new Point3(x + radius, y, 0), 1), new Vertex(new Point3(x - radius, y, 0), 1)], closed: true);

    public static Path2 Rect(double x, double y, double width, double height) =>
        Path2.FromPoints([new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)], closed: true);

    public static Path2 Line(double x1, double y1, double x2, double y2) => Path2.FromPoints([new(x1, y1), new(x2, y2)], closed: false);

    /// <summary>Arc around (x, y) from <paramref name="start"/> degrees over <paramref name="sweep"/> degrees (positive = counter-clockwise).</summary>
    public static Path2 Arc(double x, double y, double radius, double start, double sweep)
    {
        var arc = new Arc2(new Point2(x, y), radius, Angles.ToRadians(start), Angles.ToRadians(sweep));
        // Split into arcs of at most 180° (bulges stay finite).
        var n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 180));
        var vertices = new List<Vertex>();
        for (var i = 0; i < n; i++)
            vertices.Add(new Vertex(arc.PointAt((double)i / n).ToPoint3(), Math.Tan(arc.Sweep / n / 4)));
        vertices.Add(new Vertex(arc.PointAt(1).ToPoint3()));
        return new Path2(vertices, closed: false);
    }

    /// <summary>Polyline through x/y pairs.</summary>
    public static Path2 Polyline(bool closed, params double[] xy) =>
        Path2.FromPoints(Enumerable.Range(0, xy.Length / 2).Select(i => new Point2(xy[2 * i], xy[2 * i + 1])), closed);
}
