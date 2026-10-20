using System.Globalization;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Dxf;

/// <summary>
/// Minimal ASCII DXF reader: LINE, ARC, CIRCLE, LWPOLYLINE, POLYLINE/VERTEX, POINT, SPLINE, ELLIPSE,
/// and INSERT of blocks. Entities are placed on layers named after their DXF layer.
/// </summary>
public sealed class DxfReader
{
    private readonly List<(int Code, string Value)> _pairs = [];
    private int _pos;
    private readonly Dictionary<string, List<RawEntity>> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RgbColor> _layerColors = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Warnings { get; } = [];

    private sealed class RawEntity
    {
        public required string Type { get; init; }
        public List<(int Code, string Value)> Data { get; } = [];
        public List<RawEntity> Vertices { get; } = [];

        public string Layer => Get(8) ?? "0";
        public string? Get(int code) => Data.FirstOrDefault(d => d.Code == code).Value;
        public double D(int code, double fallback = 0) =>
            Data.FirstOrDefault(d => d.Code == code) is { Value: { } v } && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : fallback;
        public int I(int code) => (int)D(code);
        public IEnumerable<double> All(int code) => Data.Where(d => d.Code == code)
            .Select(d => double.TryParse(d.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0);
    }

    public static CadDocument Load(string path, out List<string> warnings)
    {
        var reader = new DxfReader();
        var doc = reader.Read(File.ReadAllLines(path));
        doc.Name = Path.GetFileNameWithoutExtension(path);
        warnings = reader.Warnings;
        return doc;
    }

    public CadDocument Read(string[] lines)
    {
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), out var code)) continue;
            _pairs.Add((code, lines[i + 1].TrimEnd()));
        }

        var doc = new CadDocument();
        var entities = new List<RawEntity>();
        while (_pos < _pairs.Count)
        {
            var (code, value) = _pairs[_pos];
            if (code == 0 && value == "SECTION" && _pos + 1 < _pairs.Count)
            {
                var name = _pairs[_pos + 1].Value;
                _pos += 2;
                switch (name)
                {
                    case "TABLES": ReadTables(); break;
                    case "BLOCKS": ReadBlocks(); break;
                    case "ENTITIES": entities = ReadEntityList("ENDSEC"); break;
                    default: SkipTo("ENDSEC"); break;
                }
                continue;
            }
            _pos++;
        }

        foreach (var raw in entities) AddEntity(doc, raw, Matrix4.Identity, 0);
        if (doc.Layers.Count == 0) doc.Layers.Add(new Layer("Default"));
        doc.ActiveLayerName = doc.Layers[0].Name;
        doc.Parts.Add(new Core.Cam.Part { Name = "Part1" });
        doc.ActivePartName = "Part1";
        doc.IsModified = false;
        return doc;
    }

    private void SkipTo(string marker)
    {
        while (_pos < _pairs.Count && !(_pairs[_pos].Code == 0 && _pairs[_pos].Value == marker)) _pos++;
        _pos++;
    }

    private void ReadTables()
    {
        while (_pos < _pairs.Count)
        {
            var (code, value) = _pairs[_pos];
            if (code == 0 && value == "ENDSEC") { _pos++; return; }
            if (code == 0 && value == "LAYER")
            {
                _pos++;
                string? name = null;
                var color = 7;
                while (_pos < _pairs.Count && _pairs[_pos].Code != 0)
                {
                    if (_pairs[_pos].Code == 2) name = _pairs[_pos].Value.Trim();
                    if (_pairs[_pos].Code == 62) int.TryParse(_pairs[_pos].Value.Trim(), out color);
                    _pos++;
                }
                if (name != null) _layerColors[name] = AciColor(Math.Abs(color));
                continue;
            }
            _pos++;
        }
    }

    private void ReadBlocks()
    {
        while (_pos < _pairs.Count)
        {
            var (code, value) = _pairs[_pos];
            if (code == 0 && value == "ENDSEC") { _pos++; return; }
            if (code == 0 && value == "BLOCK")
            {
                _pos++;
                string? name = null;
                while (_pos < _pairs.Count && _pairs[_pos].Code != 0)
                {
                    if (_pairs[_pos].Code == 2) name = _pairs[_pos].Value.Trim();
                    _pos++;
                }
                var list = ReadEntityList("ENDBLK");
                if (name != null) _blocks[name] = list;
                continue;
            }
            _pos++;
        }
    }

    private List<RawEntity> ReadEntityList(string end)
    {
        var list = new List<RawEntity>();
        RawEntity? polyline = null;
        while (_pos < _pairs.Count)
        {
            var (code, value) = _pairs[_pos];
            if (code != 0)
            {
                _pos++;
                continue;
            }
            if (value == end)
            {
                _pos++;
                break;
            }
            _pos++;
            var e = new RawEntity { Type = value };
            while (_pos < _pairs.Count && _pairs[_pos].Code != 0)
            {
                e.Data.Add(_pairs[_pos]);
                _pos++;
            }
            switch (value)
            {
                case "POLYLINE":
                    polyline = e;
                    list.Add(e);
                    break;
                case "VERTEX" when polyline != null:
                    polyline.Vertices.Add(e);
                    break;
                case "SEQEND":
                    polyline = null;
                    break;
                default:
                    list.Add(e);
                    break;
            }
        }
        return list;
    }

    private Layer LayerFor(CadDocument doc, string name)
    {
        var layer = doc.Layers.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (layer != null) return layer;
        layer = new Layer(name) { Color = _layerColors.TryGetValue(name, out var c) ? c : RgbColor.DefaultLayer };
        doc.Layers.Add(layer);
        return layer;
    }

    private void AddEntity(CadDocument doc, RawEntity e, Matrix4 m, int depth)
    {
        var layer = LayerFor(doc, e.Layer);
        Entity? result = null;
        switch (e.Type)
        {
            case "LINE":
            {
                var l = new LineEntity();
                l.Points.Add(new Point3(e.D(10), e.D(20), e.D(30)));
                l.Points.Add(new Point3(e.D(11), e.D(21), e.D(31)));
                result = l;
                break;
            }
            case "CIRCLE":
                result = new CircleEntity { Center = new Point3(e.D(10), e.D(20), e.D(30)), Diameter = 2 * e.D(40) };
                break;
            case "ARC":
            {
                var start = e.D(50);
                var end = e.D(51);
                var sweep = end - start;
                if (sweep <= 0) sweep += 360;
                result = new ArcEntity { Center = new Point3(e.D(10), e.D(20), e.D(30)), Radius = e.D(40), StartAngle = start, Sweep = sweep };
                break;
            }
            case "POINT":
            {
                var p = new PointListEntity();
                p.Points.Add(new Point3(e.D(10), e.D(20), e.D(30)));
                result = p;
                break;
            }
            case "LWPOLYLINE":
            {
                var xs = new List<double>();
                var ys = new List<double>();
                var bulges = new List<double>();
                // Bulge (42) belongs to the preceding vertex.
                foreach (var (code, value) in e.Data)
                {
                    var v = double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
                    if (code == 10) { xs.Add(v); bulges.Add(0); }
                    else if (code == 20) ys.Add(v);
                    else if (code == 42 && bulges.Count > 0) bulges[^1] = v;
                }
                var elev = e.D(38);
                var pl = new PolylineEntity { Closed = (e.I(70) & 1) != 0 };
                for (var i = 0; i < Math.Min(xs.Count, ys.Count); i++) pl.Points.Add(new Vertex(new Point3(xs[i], ys[i], elev), bulges[i]));
                result = pl;
                break;
            }
            case "POLYLINE":
            {
                var flags = e.I(70);
                if ((flags & (16 | 64)) != 0)
                {
                    Warnings.Add("Polygon meshes in DXF are not supported and were skipped.");
                    break;
                }
                var pl = new PolylineEntity { Closed = (flags & 1) != 0 };
                foreach (var v in e.Vertices)
                    pl.Points.Add(new Vertex(new Point3(v.D(10), v.D(20), v.D(30)), v.D(42)));
                result = pl;
                break;
            }
            case "SPLINE":
                result = Spline(e);
                break;
            case "ELLIPSE":
                result = Ellipse(e);
                break;
            case "INSERT":
            {
                var name = e.Get(2)?.Trim() ?? "";
                if (depth > 8 || !_blocks.TryGetValue(name, out var block))
                {
                    Warnings.Add($"Block '{name}' not found.");
                    break;
                }
                var sx = e.D(41, 1);
                var sy = e.D(42, 1);
                var t = Matrix4.Scale(sx, sy, e.D(43, 1)) * Matrix4.RotationZ(Angles.ToRadians(e.D(50))) *
                        Matrix4.Translation(e.D(10), e.D(20), e.D(30));
                foreach (var child in block) AddEntity(doc, child, t * m, depth + 1);
                return;
            }
            case "TEXT" or "MTEXT" or "DIMENSION" or "HATCH" or "SOLID" or "3DFACE" or "VIEWPORT" or "ATTDEF" or "ATTRIB":
                break;
            default:
                Warnings.Add($"DXF entity type {e.Type} is not supported.");
                break;
        }
        if (result == null) return;
        if (!m.IsIdentity) result.ApplyTransform(m);
        doc.AddEntity(result, layer);
    }

    private static PolylineEntity? Spline(RawEntity e)
    {
        var degree = Math.Max(e.I(71), 1);
        var knots = e.All(40).ToList();
        var cx = e.All(10).ToList();
        var cy = e.All(20).ToList();
        var fitX = e.All(11).ToList();
        var fitY = e.All(21).ToList();
        var pts = new List<Point2>();
        if (cx.Count >= 2 && knots.Count >= cx.Count + degree + 1)
        {
            var ctrl = cx.Zip(cy, (x, y) => new Point2(x, y)).ToList();
            var u0 = knots[degree];
            var u1 = knots[ctrl.Count];
            var steps = Math.Max(16, ctrl.Count * 12);
            for (var i = 0; i <= steps; i++) pts.Add(DeBoor(ctrl, knots, degree, u0 + (u1 - u0) * i / steps));
        }
        else if (fitX.Count >= 2)
        {
            pts.AddRange(fitX.Zip(fitY, (x, y) => new Point2(x, y)));
        }
        if (pts.Count < 2) return null;
        var closed = (e.I(70) & 1) != 0;
        if (closed && pts[0].AlmostEquals(pts[^1], 1e-9)) pts.RemoveAt(pts.Count - 1);
        return new PolylineEntity(ArcFitter.Fit(Path2.FromPoints(pts, closed), 0.005));
    }

    private static Point2 DeBoor(List<Point2> ctrl, List<double> knots, int p, double u)
    {
        var n = ctrl.Count - 1;
        var k = p;
        while (k < n && u >= knots[k + 1]) k++;
        var d = new Point2[p + 1];
        for (var j = 0; j <= p; j++) d[j] = ctrl[Math.Clamp(j + k - p, 0, n)];
        for (var r = 1; r <= p; r++)
        for (var j = p; j >= r; j--)
        {
            var i = j + k - p;
            var denom = knots[i + p - r + 1] - knots[i];
            var alpha = denom < 1e-12 ? 0 : (u - knots[i]) / denom;
            d[j] = Point2.Lerp(d[j - 1], d[j], alpha);
        }
        return d[p];
    }

    private static PolylineEntity Ellipse(RawEntity e)
    {
        var c = new Point2(e.D(10), e.D(20));
        var major = new Point2(e.D(11), e.D(21));
        var ratio = e.D(40, 1);
        var t0 = e.D(41, 0);
        var t1 = e.D(42, Math.PI * 2);
        if (t1 <= t0) t1 += Math.PI * 2;
        var minor = major.Perp * ratio;
        var full = Math.Abs(t1 - t0 - Math.PI * 2) < 1e-6;
        var steps = 96;
        var pts = new List<Point2>();
        for (var i = 0; i <= (full ? steps - 1 : steps); i++)
        {
            var t = t0 + (t1 - t0) * i / steps;
            pts.Add(c + major * Math.Cos(t) + minor * Math.Sin(t));
        }
        return new PolylineEntity(ArcFitter.Fit(Path2.FromPoints(pts, full), 0.005));
    }

    private static RgbColor AciColor(int aci) => aci switch
    {
        1 => new RgbColor(255, 0, 0),
        2 => new RgbColor(255, 255, 0),
        3 => new RgbColor(0, 255, 0),
        4 => new RgbColor(0, 255, 255),
        5 => new RgbColor(0, 0, 255),
        6 => new RgbColor(255, 0, 255),
        8 => new RgbColor(128, 128, 128),
        9 => new RgbColor(192, 192, 192),
        _ => RgbColor.White,
    };
}
