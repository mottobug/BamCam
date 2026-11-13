using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.IO.Svg;

/// <summary>
/// SVG reader for drawings from Inkscape, Illustrator, Affinity and similar programs: path, rect, circle, ellipse,
/// line, polyline, polygon, groups with transforms and use. Each subpath becomes a polyline; Bézier curves and
/// elliptical arcs are approximated with lines and arcs (bulges). Circles stay circles and rectangles stay rectangles
/// where the transform allows it. The page keeps its size: its lower left corner is the origin, Y points up. Inkscape
/// layers (and the named top-level groups of other programs) become layers. Text and images are skipped with a warning.
/// </summary>
public sealed class SvgReader
{
    private const double MmPerInch = 25.4;
    /// <summary>Deviation of the points of a curve from the exact curve, in mm.</summary>
    private const double FlattenTolerance = 0.001;
    /// <summary>Tolerance of the arcs fitted to the points, in mm (as for DXF fit-point splines).</summary>
    private const double FitTolerance = 0.005;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Inkscape = "http://www.inkscape.org/namespaces/inkscape";

    private readonly DrawingUnits _units;
    private readonly Dictionary<string, XElement> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
    private CadDocument _doc = null!;
    private string _fileLayer = "SVG";
    private bool _isInkscape;
    /// <summary>Output units per mm.</summary>
    private double _perMm = 1;

    public SvgReader(DrawingUnits units = DrawingUnits.Millimeters) => _units = units;

    public List<string> Warnings { get; } = [];

    public static CadDocument Load(string path, DrawingUnits units, out List<string> warnings)
    {
        var reader = new SvgReader(units);
        var doc = reader.Read(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        doc.Name = Path.GetFileNameWithoutExtension(path);
        warnings = reader.Warnings;
        return doc;
    }

    public CadDocument Read(string text, string name = "SVG")
    {
        var xml = XDocument.Parse(text, LoadOptions.None);
        var root = xml.Root ?? throw new InvalidDataException("Empty SVG file.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Not an SVG file (no <svg> element).");

        _doc = new CadDocument { Units = _units };
        _fileLayer = string.IsNullOrWhiteSpace(name) ? "SVG" : name;
        _perMm = _units == DrawingUnits.Inches ? 1 / MmPerInch : 1;
        _isInkscape = root.Attributes().Any(a => a.IsNamespaceDeclaration && (a.Value == Inkscape.NamespaceName || a.Value.Contains("sodipodi")));
        foreach (var e in root.DescendantsAndSelf())
            if ((string?)e.Attribute("id") is { Length: > 0 } id) _ids.TryAdd(id, e);

        var (page, pageHeight) = PageTransform(root);
        foreach (var child in root.Elements()) Walk(child, page, null, 0, topLevel: true);

        if (pageHeight == null && _doc.AllEntities().Any())
        {
            // No page size: the lowest point goes to Y = 0.
            var minY = _doc.AllEntities().Select(e => e.Bounds()).Where(b => !b.IsEmpty).Select(b => b.Min.Y).DefaultIfEmpty(0).Min();
            if (Math.Abs(minY) > 1e-12)
                foreach (var e in _doc.AllEntities()) e.ApplyTransform(Matrix4.Translation(0, -minY));
        }

        foreach (var (what, count) in _skipped)
            Warnings.Add(what switch
            {
                "text" => Tr.F("{0} texts were skipped: convert text to paths first (Inkscape: Path ▸ Object to Path).", count),
                "image" => Tr.F("{0} embedded images were skipped: import them with File ▸ Import Image.", count),
                "hidden" => Tr.F("{0} hidden objects or layers were skipped.", count),
                _ => Tr.F("{0} SVG elements <{1}> are not supported and were skipped.", count, what),
            });

        if (_doc.Layers.Count == 0) _doc.Layers.Add(new Layer(_fileLayer));
        _doc.ActiveLayerName = _doc.Layers[0].Name;
        _doc.Parts.Add(new Core.Cam.Part { Name = "Part1" });
        _doc.ActivePartName = "Part1";
        _doc.IsModified = false;
        return _doc;
    }

    // ------------------------------------------------------------------ page, units, transforms

    /// <summary>
    /// The transform from SVG user units to drawing units: viewBox and width/height (with their units; plain numbers
    /// are CSS pixels, 96 per inch), then Y flipped about the page height. Null height = page size unknown.
    /// </summary>
    private (Affine Transform, double? Height) PageTransform(XElement root)
    {
        var widthMm = LengthMm((string?)root.Attribute("width"));
        var heightMm = LengthMm((string?)root.Attribute("height"));
        var vb = Numbers((string?)root.Attribute("viewBox") ?? "");
        const double pxMm = MmPerInch / 96;

        double sx, sy, tx = 0, ty = 0;
        if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0)
        {
            widthMm ??= vb[2] * pxMm * (heightMm is { } h0 ? h0 / (vb[3] * pxMm) : 1);
            heightMm ??= vb[3] * (widthMm.Value / vb[2]);
            sx = widthMm.Value / vb[2];
            sy = heightMm.Value / vb[3];
            var aspect = ((string?)root.Attribute("preserveAspectRatio") ?? "xMidYMid meet").Trim();
            if (!aspect.StartsWith("none", StringComparison.Ordinal))
            {
                // meet (default) or slice: one scale, aligned as given.
                var s = aspect.Contains("slice") ? Math.Max(sx, sy) : Math.Min(sx, sy);
                var freeX = widthMm.Value - vb[2] * s;
                var freeY = heightMm.Value - vb[3] * s;
                tx = aspect.Contains("xMid") ? freeX / 2 : aspect.Contains("xMax") ? freeX : 0;
                ty = aspect.Contains("YMid") ? freeY / 2 : aspect.Contains("YMax") ? freeY : 0;
                sx = sy = s;
            }
            tx -= vb[0] * sx;
            ty -= vb[1] * sy;
        }
        else
        {
            sx = sy = pxMm;
        }

        // SVG: Y down from the top of the page. Drawing: Y up from the bottom.
        var height = heightMm ?? 0;
        var toMm = new Affine(sx, 0, 0, -sy, tx, height - ty);
        var scale = new Affine(_perMm, 0, 0, _perMm, 0, 0);
        return (scale * toMm, heightMm == null ? null : height * _perMm);
    }

    /// <summary>An SVG length in mm; null for none or a percentage.</summary>
    private static double? LengthMm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-z%]*)$");
        if (!m.Success) return null;
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "" or "px" => v * MmPerInch / 96,
            "mm" => v,
            "cm" => v * 10,
            "in" => v * MmPerInch,
            "pt" => v * MmPerInch / 72,
            "pc" => v * MmPerInch / 6,
            "q" => v / 4,
            _ => null,
        };
    }

    private static readonly Regex NumberRx = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private static List<double> Numbers(string s) =>
        NumberRx.Matches(s).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

    private static Affine ParseTransform(string? s)
    {
        var result = Affine.Identity;
        if (string.IsNullOrWhiteSpace(s)) return result;
        foreach (Match m in Regex.Matches(s, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
        {
            var a = Numbers(m.Groups[2].Value);
            double A(int i, double d = 0) => i < a.Count ? a[i] : d;
            var t = m.Groups[1].Value switch
            {
                "matrix" when a.Count >= 6 => new Affine(a[0], a[1], a[2], a[3], a[4], a[5]),
                "translate" => new Affine(1, 0, 0, 1, A(0), A(1)),
                "scale" => new Affine(A(0, 1), 0, 0, A(1, A(0, 1)), 0, 0),
                "rotate" => Rotate(A(0), A(1), A(2)),
                "skewX" => new Affine(1, 0, Math.Tan(A(0) * Math.PI / 180), 1, 0, 0),
                "skewY" => new Affine(1, Math.Tan(A(0) * Math.PI / 180), 0, 1, 0, 0),
                _ => Affine.Identity,
            };
            // The list applies from right to left: "translate(..) rotate(..)" rotates first.
            result *= t;
        }
        return result;
    }

    private static Affine Rotate(double degrees, double cx, double cy)
    {
        var r = degrees * Math.PI / 180;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        return new Affine(1, 0, 0, 1, cx, cy) * new Affine(c, s, -s, c, 0, 0) * new Affine(1, 0, 0, 1, -cx, -cy);
    }

    // ------------------------------------------------------------------ the element tree

    /// <summary>Inline style or presentation attribute.</summary>
    private static string? Style(XElement e, string name)
    {
        if ((string?)e.Attribute("style") is { } style)
            foreach (var part in style.Split(';'))
            {
                var kv = part.Split(':', 2);
                if (kv.Length == 2 && kv[0].Trim() == name) return kv[1].Trim();
            }
        return (string?)e.Attribute(name);
    }

    private static bool Hidden(XElement e) => Style(e, "display") == "none" || Style(e, "visibility") is "hidden" or "collapse";

    private void Skip(string what) => _skipped[what] = _skipped.GetValueOrDefault(what) + 1;

    private void Walk(XElement e, Affine parent, Layer? layer, int depth, bool topLevel = false)
    {
        if (e.Name.Namespace != Svg && e.Name.Namespace != XNamespace.None) return; // sodipodi:namedview, metadata of editors
        var tag = e.Name.LocalName;
        if (tag is "defs" or "metadata" or "title" or "desc" or "style" or "script" or "symbol" or "clipPath" or "mask"
            or "marker" or "pattern" or "linearGradient" or "radialGradient" or "filter") return;
        if (Hidden(e))
        {
            Skip("hidden");
            return;
        }
        var m = parent * ParseTransform((string?)e.Attribute("transform"));

        switch (tag)
        {
            case "g" or "svg" or "a" or "switch":
                if (LayerName(e, topLevel) is { } layerName) layer = LayerFor(layerName);
                foreach (var child in e.Elements()) Walk(child, m, layer, depth + 1);
                return;
            case "use":
            {
                var href = ((string?)e.Attribute(XLink + "href") ?? (string?)e.Attribute("href") ?? "").TrimStart('#');
                if (depth > 16 || !_ids.TryGetValue(href, out var target) || target == e) return;
                var at = m * new Affine(1, 0, 0, 1, Num(e, "x"), Num(e, "y"));
                if (target.Name.LocalName == "symbol")
                    foreach (var child in target.Elements()) Walk(child, at, layer, depth + 1);
                else
                    Walk(target, at, layer, depth + 1);
                return;
            }
            case "path":
                AddCurves(ParsePath((string?)e.Attribute("d") ?? ""), m, layer);
                return;
            case "rect":
                Rect(e, m, layer);
                return;
            case "circle":
            {
                var r = Num(e, "r");
                if (r > 0) Ellipse(Num(e, "cx"), Num(e, "cy"), r, r, m, layer);
                return;
            }
            case "ellipse":
            {
                var rx = Num(e, "rx");
                var ry = Num(e, "ry");
                if (rx > 0 && ry > 0) Ellipse(Num(e, "cx"), Num(e, "cy"), rx, ry, m, layer);
                return;
            }
            case "line":
                AddPaths([new SubPath([new Point2(Num(e, "x1"), Num(e, "y1")), new Point2(Num(e, "x2"), Num(e, "y2"))], false)], m, layer);
                return;
            case "polyline" or "polygon":
            {
                var n = Numbers((string?)e.Attribute("points") ?? "");
                var pts = Enumerable.Range(0, n.Count / 2).Select(i => new Point2(n[2 * i], n[2 * i + 1])).ToList();
                if (pts.Count >= 2) AddPaths([new SubPath(pts, tag == "polygon")], m, layer);
                return;
            }
            case "text" or "flowRoot":
                Skip("text");
                return;
            case "image":
                Skip("image");
                return;
            default:
                if (e.Name.Namespace == Svg) Skip(tag);
                return;
        }
    }

    /// <summary>
    /// Layers: Inkscape layers by their label; in files of other programs the named top-level groups (Illustrator writes
    /// its layers as &lt;g id="Layer_1"&gt;). Null = stay on the layer of the parent.
    /// </summary>
    private string? LayerName(XElement g, bool topLevel)
    {
        if (g.Name.LocalName != "g") return null;
        if ((string?)g.Attribute(Inkscape + "groupmode") == "layer")
            return (string?)g.Attribute(Inkscape + "label") is { Length: > 0 } label ? label : (string?)g.Attribute("id");
        if (_isInkscape || !topLevel) return null;
        return (string?)g.Attribute("id") is { Length: > 0 } id ? id.Replace('_', ' ').Trim() : null;
    }

    private Layer LayerFor(string name)
    {
        var layer = _doc.Layers.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (layer != null) return layer;
        layer = new Layer(name);
        _doc.Layers.Add(layer);
        return layer;
    }

    private Layer Target(Layer? layer) => layer ?? LayerFor(_fileLayer);

    private static double Num(XElement e, string name)
    {
        var s = (string?)e.Attribute(name);
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var n = Numbers(s);
        // Lengths in mm etc. inside the drawing are rare; plain user units are assumed.
        return n.Count > 0 ? n[0] : 0;
    }

    // ------------------------------------------------------------------ shapes

    private void Rect(XElement e, Affine m, Layer? layer)
    {
        double x = Num(e, "x"), y = Num(e, "y"), w = Num(e, "width"), h = Num(e, "height");
        if (w <= 0 || h <= 0) return;
        var rxAttr = e.Attribute("rx") != null;
        var ryAttr = e.Attribute("ry") != null;
        var rx = rxAttr ? Num(e, "rx") : Num(e, "ry");
        var ry = ryAttr ? Num(e, "ry") : rx;
        rx = Math.Min(Math.Max(rx, 0), w / 2);
        ry = Math.Min(Math.Max(ry, 0), h / 2);

        // A rectangle entity where the transform keeps it axis-parallel (and the corners round, not elliptical).
        var square = rx == 0 || ry == 0;
        var roundCorners = Math.Abs(rx - ry) < 1e-12 && Math.Abs(Math.Abs(m.A) - Math.Abs(m.D)) <= 1e-9 * Math.Abs(m.A);
        if (m.IsAxisAligned && (square || roundCorners))
        {
            var a = m.Apply(new Point2(x, y));
            var b = m.Apply(new Point2(x + w, y + h));
            _doc.AddEntity(RectEntity.FromCorners(a, b, square ? 0 : rx * Math.Abs(m.A)), Target(layer));
            return;
        }
        if (square)
        {
            AddPaths([new SubPath([new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)], true)], m, layer);
            return;
        }
        // Rounded corners with quarter ellipses (cubic approximation).
        var sp = new SubPathBuilder(new Point2(x + rx, y));
        sp.Line(new Point2(x + w - rx, y));
        sp.Arc(rx, ry, 0, false, true, new Point2(x + w, y + ry));
        sp.Line(new Point2(x + w, y + h - ry));
        sp.Arc(rx, ry, 0, false, true, new Point2(x + w - rx, y + h));
        sp.Line(new Point2(x + rx, y + h));
        sp.Arc(rx, ry, 0, false, true, new Point2(x, y + h - ry));
        sp.Line(new Point2(x, y + ry));
        sp.Arc(rx, ry, 0, false, true, new Point2(x + rx, y));
        AddCurves([sp.Close()], m, layer);
    }

    private void Ellipse(double cx, double cy, double rx, double ry, Affine m, Layer? layer)
    {
        if (Math.Abs(rx - ry) < 1e-12 && m.IsSimilarity)
        {
            var c = m.Apply(new Point2(cx, cy));
            _doc.AddEntity(new CircleEntity { Center = c.ToPoint3(0), Diameter = 2 * rx * m.ScaleFactor }, Target(layer));
            return;
        }
        var sp = new SubPathBuilder(new Point2(cx + rx, cy));
        sp.Arc(rx, ry, 0, false, true, new Point2(cx, cy + ry));
        sp.Arc(rx, ry, 0, false, true, new Point2(cx - rx, cy));
        sp.Arc(rx, ry, 0, false, true, new Point2(cx, cy - ry));
        sp.Arc(rx, ry, 0, false, true, new Point2(cx + rx, cy));
        AddCurves([sp.Close()], m, layer);
    }

    private void AddPaths(IEnumerable<SubPath> paths, Affine m, Layer? layer) =>
        AddCurves(paths.Select(p => new CurvePath(p.Points[0], p.Points.Skip(1).Select(q => new Seg(q)).ToList(), p.Closed)), m, layer);

    /// <summary>One polyline per subpath: transformed, curves split into points, then lines and arcs fitted.</summary>
    private void AddCurves(IEnumerable<CurvePath> paths, Affine m, Layer? layer)
    {
        var tol = FlattenTolerance * _perMm;
        foreach (var path in paths)
        {
            var pts = new List<Point2> { m.Apply(path.Start) };
            var cur = path.Start;
            var curved = false;
            foreach (var seg in path.Segments)
            {
                if (seg.C1 is { } c1 && seg.C2 is { } c2)
                {
                    curved = true;
                    Flatten(m.Apply(cur), m.Apply(c1), m.Apply(c2), m.Apply(seg.To), tol, pts);
                }
                else pts.Add(m.Apply(seg.To));
                cur = seg.To;
            }
            var clean = new List<Point2>();
            foreach (var p in pts)
                if (clean.Count == 0 || !clean[^1].AlmostEquals(p, 1e-9)) clean.Add(p);
            var closed = path.Closed;
            if (clean.Count > 2 && clean[0].AlmostEquals(clean[^1], 1e-6))
            {
                clean.RemoveAt(clean.Count - 1);
                closed = true;
            }
            if (clean.Count < 2) continue;
            var p2 = Path2.FromPoints(clean, closed);
            if (curved) p2 = ArcFitter.Fit(p2, FitTolerance * _perMm);
            _doc.AddEntity(new PolylineEntity(p2), Target(layer));
        }
    }

    /// <summary>Points of a cubic Bézier curve (without its start point), close enough for <paramref name="tol"/>.</summary>
    private static void Flatten(Point2 p0, Point2 p1, Point2 p2, Point2 p3, double tol, List<Point2> pts)
    {
        // Largest second difference bounds the deviation of the chords: |d2| * 6 / (8 n²) ≤ tol.
        var d1 = p0 - p1 * 2 + p2;
        var d2 = p1 - p2 * 2 + p3;
        var dd = 6 * Math.Max(d1.Length, d2.Length);
        var n = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(dd / (8 * tol))), 1, 2000);
        for (var i = 1; i <= n; i++)
        {
            var t = (double)i / n;
            var u = 1 - t;
            pts.Add(p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t));
        }
    }

    // ------------------------------------------------------------------ path data

    private sealed record SubPath(List<Point2> Points, bool Closed);

    /// <summary>A segment to <see cref="To"/>: a line, or a cubic Bézier curve with control points C1 and C2.</summary>
    private readonly record struct Seg(Point2 To, Point2? C1 = null, Point2? C2 = null);

    private sealed record CurvePath(Point2 Start, List<Seg> Segments, bool Closed);

    private sealed class SubPathBuilder(Point2 start)
    {
        public Point2 Start { get; } = start;
        public Point2 Current { get; private set; } = start;
        public List<Seg> Segments { get; } = [];

        public void Line(Point2 to)
        {
            Segments.Add(new Seg(to));
            Current = to;
        }

        public void Cubic(Point2 c1, Point2 c2, Point2 to)
        {
            Segments.Add(new Seg(to, c1, c2));
            Current = to;
        }

        /// <summary>SVG elliptical arc from the current point, as cubic Bézier curves of at most 90°.</summary>
        public void Arc(double rx, double ry, double rotationDeg, bool large, bool sweep, Point2 to)
        {
            var from = Current;
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (from.AlmostEquals(to, 1e-12)) return;
            if (rx < 1e-12 || ry < 1e-12)
            {
                Line(to);
                return;
            }
            var phi = rotationDeg * Math.PI / 180;
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            // Endpoint to centre parameterisation (SVG implementation notes, F.6.5).
            var dx = (from.X - to.X) / 2;
            var dy = (from.Y - to.Y) / 2;
            var x1 = cos * dx + sin * dy;
            var y1 = -sin * dx + cos * dy;
            var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1)
            {
                var s = Math.Sqrt(lambda);
                rx *= s;
                ry *= s;
            }
            var num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            var den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            var coef = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
            var cxp = coef * rx * y1 / ry;
            var cyp = -coef * ry * x1 / rx;
            var cx = cos * cxp - sin * cyp + (from.X + to.X) / 2;
            var cy = sin * cxp + cos * cyp + (from.Y + to.Y) / 2;
            double Angle(double ux, double uy, double vx, double vy) =>
                Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            var theta = Angle(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
            var delta = Angle((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
            if (!sweep && delta > 0) delta -= 2 * Math.PI;
            else if (sweep && delta < 0) delta += 2 * Math.PI;

            var parts = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9);
            parts = Math.Max(parts, 1);
            var step = delta / parts;
            var k = 4.0 / 3 * Math.Tan(step / 4);
            Point2 P(double t) => new(cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin, cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos);
            Point2 D(double t) => new(-rx * Math.Sin(t) * cos - ry * Math.Cos(t) * sin, -rx * Math.Sin(t) * sin + ry * Math.Cos(t) * cos);
            var t0 = theta;
            for (var i = 0; i < parts; i++)
            {
                var t1 = t0 + step;
                var end = i == parts - 1 ? to : P(t1);
                Cubic(P(t0) + D(t0) * k, P(t1) - D(t1) * k, end);
                t0 = t1;
            }
        }

        public CurvePath Close() => new(Start, Segments, true);
        public CurvePath Open() => new(Start, Segments, false);
    }

    private static readonly Regex TokenRx = new(@"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <summary>The subpaths of path data ("d"), all segments as lines or cubic curves.</summary>
    private static List<CurvePath> ParsePath(string d)
    {
        var result = new List<CurvePath>();
        var tokens = Tokenize(d);
        var i = 0;
        SubPathBuilder? sp = null;
        var cur = Point2.Zero;
        var start = Point2.Zero;
        Point2? lastCubic = null, lastQuad = null;
        var cmd = ' ';

        bool HasNumber() => i < tokens.Count && tokens[i].IsNumber;
        double N() => tokens[i++].Value;

        void Finish(bool closed)
        {
            if (sp != null && sp.Segments.Count > 0) result.Add(closed ? sp.Close() : sp.Open());
            sp = null;
        }

        SubPathBuilder Ensure()
        {
            sp ??= new SubPathBuilder(cur);
            return sp;
        }

        while (i < tokens.Count)
        {
            if (!tokens[i].IsNumber) cmd = tokens[i++].Command;
            else if (cmd == ' ') { i++; continue; }
            var rel = char.IsLower(cmd);
            Point2 Pt(double x, double y) => rel ? new Point2(cur.X + x, cur.Y + y) : new Point2(x, y);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    if (!HasNumber()) break;
                    Finish(false);
                    var x = N();
                    if (!HasNumber()) break;
                    cur = Pt(x, N());
                    start = cur;
                    sp = new SubPathBuilder(cur);
                    // Further pairs are lines.
                    cmd = rel ? 'l' : 'L';
                    lastCubic = lastQuad = null;
                    continue;
                }
                case 'L':
                {
                    if (!HasNumber()) break;
                    var x = N();
                    if (!HasNumber()) break;
                    cur = Pt(x, N());
                    Ensure().Line(cur);
                    lastCubic = lastQuad = null;
                    continue;
                }
                case 'H':
                    if (!HasNumber()) break;
                    cur = rel ? cur with { X = cur.X + N() } : cur with { X = N() };
                    Ensure().Line(cur);
                    lastCubic = lastQuad = null;
                    continue;
                case 'V':
                    if (!HasNumber()) break;
                    cur = rel ? cur with { Y = cur.Y + N() } : cur with { Y = N() };
                    Ensure().Line(cur);
                    lastCubic = lastQuad = null;
                    continue;
                case 'C':
                {
                    if (!HasSix()) break;
                    var c1 = Pt(N(), N());
                    var c2 = Pt(N(), N());
                    var to = Pt(N(), N());
                    Ensure().Cubic(c1, c2, to);
                    cur = to;
                    lastCubic = c2;
                    lastQuad = null;
                    continue;
                }
                case 'S':
                {
                    if (!HasFour()) break;
                    var c1 = lastCubic is { } lc ? cur * 2 - lc : cur;
                    var c2 = Pt(N(), N());
                    var to = Pt(N(), N());
                    Ensure().Cubic(c1, c2, to);
                    cur = to;
                    lastCubic = c2;
                    lastQuad = null;
                    continue;
                }
                case 'Q':
                {
                    if (!HasFour()) break;
                    var q = Pt(N(), N());
                    var to = Pt(N(), N());
                    Quad(q, to);
                    continue;
                }
                case 'T':
                {
                    if (!HasTwo()) break;
                    var q = lastQuad is { } lq ? cur * 2 - lq : cur;
                    var to = Pt(N(), N());
                    Quad(q, to);
                    continue;
                }
                case 'A':
                {
                    if (!HasSeven()) break;
                    var rx = N();
                    var ry = N();
                    var rot = N();
                    var large = N() != 0;
                    var sweep = N() != 0;
                    var to = Pt(N(), N());
                    Ensure().Arc(rx, ry, rot, large, sweep, to);
                    cur = to;
                    lastCubic = lastQuad = null;
                    continue;
                }
                case 'Z':
                    if (sp != null && sp.Segments.Count > 0)
                    {
                        if (!cur.AlmostEquals(start, 1e-12)) sp.Line(start);
                        Finish(true);
                    }
                    else sp = null;
                    cur = start;
                    lastCubic = lastQuad = null;
                    // A command after Z without M starts at the same point.
                    if (HasNumber()) cmd = ' ';
                    continue;
            }
            // Missing numbers: skip to the next command.
            while (i < tokens.Count && tokens[i].IsNumber) i++;
            continue;

            bool HasTwo() => i + 1 < tokens.Count && tokens[i].IsNumber && tokens[i + 1].IsNumber;
            bool HasFour() => HasCount(4);
            bool HasSix() => HasCount(6);
            bool HasSeven() => HasCount(7);
            bool HasCount(int n)
            {
                for (var k = 0; k < n; k++)
                    if (i + k >= tokens.Count || !tokens[i + k].IsNumber) return false;
                return true;
            }
            void Quad(Point2 q, Point2 to)
            {
                // Quadratic as cubic: control points two thirds towards q.
                Ensure().Cubic(cur + (q - cur) * (2.0 / 3), to + (q - to) * (2.0 / 3), to);
                cur = to;
                lastQuad = q;
                lastCubic = null;
            }
        }
        Finish(false);
        return result;
    }

    private readonly record struct Token(char Command, double Value, bool IsNumber);

    /// <summary>
    /// Commands and numbers of path data. Arc flags may be written without separators ("a1 1 0 01 5 5"): a number
    /// starting with 0 or 1 in a flag position is split there.
    /// </summary>
    private static List<Token> Tokenize(string d)
    {
        var tokens = new List<Token>();
        var cmd = ' ';
        var argIndex = 0;
        var pos = 0;
        while (pos < d.Length)
        {
            var c = d[pos];
            if (char.IsWhiteSpace(c) || c == ',') { pos++; continue; }
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                cmd = c;
                argIndex = 0;
                tokens.Add(new Token(c, 0, false));
                pos++;
                continue;
            }
            if (char.ToUpperInvariant(cmd) == 'A' && argIndex % 7 is 3 or 4 && c is '0' or '1')
            {
                tokens.Add(new Token(' ', c - '0', true));
                argIndex++;
                pos++;
                continue;
            }
            var m = NumberRx.Match(d, pos);
            if (!m.Success || m.Index != pos) { pos++; continue; }
            tokens.Add(new Token(' ', double.Parse(m.Value, CultureInfo.InvariantCulture), true));
            argIndex++;
            pos += m.Length;
        }
        return tokens;
    }
}

/// <summary>2D affine transform as in SVG: x' = a x + c y + e, y' = b x + d y + f.</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>p * q applies q first, then p.</summary>
    public static Affine operator *(Affine p, Affine q) => new(
        p.A * q.A + p.C * q.B, p.B * q.A + p.D * q.B,
        p.A * q.C + p.C * q.D, p.B * q.C + p.D * q.D,
        p.A * q.E + p.C * q.F + p.E, p.B * q.E + p.D * q.F + p.F);

    public Point2 Apply(Point2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>Rotation (or mirror) with one scale: circles stay circles.</summary>
    public bool IsSimilarity => Math.Abs(A * A + B * B - (C * C + D * D)) < 1e-9 * (A * A + B * B) && Math.Abs(A * C + B * D) < 1e-9 * (A * A + B * B);

    /// <summary>No rotation or skew: rectangles stay axis-parallel.</summary>
    public bool IsAxisAligned => Math.Abs(B) < 1e-12 && Math.Abs(C) < 1e-12;

    public double ScaleFactor => Math.Sqrt(Math.Abs(A * D - B * C));
}
