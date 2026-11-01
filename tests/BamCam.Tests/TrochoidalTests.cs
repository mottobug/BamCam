using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class TrochoidalTests
{
    private static readonly Path2 Pocket = Path2.FromPoints([new(0, 0), new(40, 0), new(40, 20), new(0, 20)], closed: true);
    private static readonly Path2 Island = Path2.FromPoints([new(25, 7), new(31, 7), new(31, 13), new(25, 13)], closed: true);

    private static (CadDocument, TrochoidalPocketOp, Toolpath) Generate(Action<TrochoidalPocketOp>? setup = null, bool island = false)
    {
        var doc = new CadDocument();
        var op = new TrochoidalPocketOp { Name = "troch" };
        foreach (var p in island ? new[] { Pocket, Island } : [Pocket])
        {
            var e = new PolylineEntity(p);
            doc.AddEntity(e);
            op.PrimitiveIds.Add(e.Id);
        }
        op.ToolDiameter.Set(6);
        op.TargetDepth.Set(-4);
        op.DepthIncrement.Set(4);
        op.StepOver.Set(0.2);
        setup?.Invoke(op);
        doc.ActivePart.Operations.Add(op);
        return (doc, op, ToolpathGenerator.Generate(doc, op));
    }

    /// <summary>Tool centre positions along all cutting moves (arcs sampled).</summary>
    private static List<Point3> Centres(Toolpath tp) =>
        tp.DisplaySegments(0.01).Where(s => s.Kind != MoveKind.Rapid).SelectMany(s => new[] { s.From, s.To }).ToList();

    [Fact]
    public void Tool_stays_inside_and_clears_the_pocket()
    {
        var (_, _, tp) = Generate(island: true);
        Assert.Empty(tp.Warnings);
        var centres = Centres(tp).Where(p => p.Z < -3.999).ToList();
        Assert.NotEmpty(centres);
        foreach (var c in centres)
        {
            Assert.True(Pocket.Contains(c.XY) && Pocket.DistanceTo(c.XY) >= 3 - 0.02, $"{c} too close to the wall");
            Assert.True(!Island.Contains(c.XY) && Island.DistanceTo(c.XY) >= 3 - 0.02, $"{c} too close to the island");
        }
        // Every point of the pocket (away from the corners the round tool cannot reach) is cut.
        var grid = new SpatialGrid(centres.Select(c => c.XY), 1);
        for (var x = 0.5; x < 40; x += 1)
        for (var y = 0.5; y < 20; y += 1)
        {
            var p = new Point2(x, y);
            if (Island.Contains(p) || Island.DistanceTo(p) < 0.5) continue;
            var nearCorner = new[] { new Point2(0, 0), new Point2(40, 0), new Point2(40, 20), new Point2(0, 20) }.Any(k => Point2.Distance(k, p) < 2.5);
            if (nearCorner) continue;
            Assert.True(grid.Nearest(p) <= 3 + 0.05, $"{p} not cut");
        }
    }

    [Fact]
    public void Circles_at_every_depth_level()
    {
        var (_, _, tp) = Generate(o => o.DepthIncrement.Set(2));
        var levels = tp.Moves.Where(m => m.IsArc).Select(m => Math.Round(m.To.Z, 6)).Distinct().ToList();
        Assert.Contains(-2.0, levels);
        Assert.Contains(-4.0, levels);
        Assert.True(tp.Moves.Count(m => m.IsArc) > 20, "circles expected");
        Assert.All(tp.Moves.Where(m => m.IsMotion), m => Assert.True(m.To.Z >= -4 - 1e-9));
    }

    [Fact]
    public void Too_small_shapes_warn_and_the_operation_round_trips()
    {
        var (doc, op, _) = Generate(o => o.ToolDiameter.Set(30));
        Assert.Contains(ToolpathGenerator.Generate(doc, op).Warnings, w => w.Contains("troch"));
        op.ToolDiameter.Set(6);
        op.MinCircleRadius.Set(0.3);
        var back = CbReader.Read(System.Xml.Linq.XDocument.Parse(CbWriter.ToString(doc)));
        var t = Assert.IsType<TrochoidalPocketOp>(Assert.Single(back.ActivePart.Operations));
        Assert.Equal(0.3, t.MinCircleRadius.Value, 9);
        Assert.Equal(MillingDirection.Climb, t.MillingDirection.Value);
    }

    /// <summary>Nearest-distance lookup over many points.</summary>
    private sealed class SpatialGrid
    {
        private readonly Dictionary<(int, int), List<Point2>> _cells = [];
        private readonly double _size;

        public SpatialGrid(IEnumerable<Point2> points, double size)
        {
            _size = size;
            foreach (var p in points)
            {
                var key = ((int)Math.Floor(p.X / size), (int)Math.Floor(p.Y / size));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(p);
            }
        }

        public double Nearest(Point2 p)
        {
            var best = double.PositiveInfinity;
            int cx = (int)Math.Floor(p.X / _size), cy = (int)Math.Floor(p.Y / _size);
            for (var dx = -4; dx <= 4; dx++)
            for (var dy = -4; dy <= 4; dy++)
                if (_cells.TryGetValue((cx + dx, cy + dy), out var list))
                    foreach (var q in list) best = Math.Min(best, Point2.Distance(p, q));
            return best;
        }
    }
}
