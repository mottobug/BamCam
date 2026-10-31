using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Dxf;

namespace BamCam.Tests;

public class DxfExportTests
{
    private static CadDocument Drawing()
    {
        var doc = new CadDocument();
        doc.AddEntity(new CircleEntity { Center = new Point3(10, 10, 0), Diameter = 6 });
        doc.AddEntity(new PolylineEntity(new Path2([new Vertex(new Point3(0, 0, 0), 0.5), new Vertex(new Point3(20, 0, 0)), new Vertex(new Point3(20, 5, 0))], true)));
        doc.AddEntity(SplineEditing.FromAnchors([new(0, 30, 0), new(10, 40, 0), new(20, 30, 0), new(30, 40, 0)], closed: false));
        var points = new PointListEntity();
        points.Points.AddRange([new Point3(1, 2, 0), new Point3(3, 4, 0)]);
        doc.AddEntity(points);
        return doc;
    }

    private static CadDocument RoundTrip(CadDocument doc, DxfVersion version)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bamcam-dxf-{Guid.NewGuid():N}.dxf");
        try
        {
            DxfWriter.Save(doc, file, version: version);
            var back = DxfReader.Load(file, out var warnings);
            Assert.Empty(warnings);
            return back;
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Dxf_2000_keeps_splines()
    {
        var doc = Drawing();
        var text = DxfWriter.ToString(doc, version: DxfVersion.R2000);
        Assert.Contains("AC1015", text);
        Assert.Contains("\r\nSPLINE\r\n", text);
        Assert.Contains("\r\nLWPOLYLINE\r\n", text);

        var back = RoundTrip(doc, DxfVersion.R2000);
        var original = doc.AllEntities().OfType<SplineEntity>().Single();
        var spline = Assert.Single(back.AllEntities().OfType<SplineEntity>());
        Assert.Equal(original.Degree, spline.Degree);
        Assert.Equal(original.Knots, spline.Knots);
        // Written with 10 decimals.
        Assert.All(original.ControlPoints.Zip(spline.ControlPoints), p => Assert.True(Point3.Distance(p.First, p.Second) < 1e-9));
        Assert.Single(back.AllEntities().OfType<CircleEntity>());
        Assert.Equal(2, back.AllEntities().OfType<PointListEntity>().Sum(p => p.Points.Count));
        var pline = Assert.Single(back.AllEntities().OfType<PolylineEntity>());
        Assert.True(pline.Closed);
        Assert.Equal(0.5, pline.Points[0].Bulge, 9);
    }

    [Fact]
    public void Dxf_2000_has_unique_handles_and_a_matching_handseed()
    {
        var lines = DxfWriter.ToString(Drawing(), version: DxfVersion.R2000).Split("\r\n");
        var handles = new List<string>();
        // Object handles come after the header (whose $HANDSEED is the next free handle).
        var headerEnd = Array.IndexOf(lines, "ENDSEC");
        for (var i = headerEnd + 1; i + 1 < lines.Length; i += 2)
            if (lines[i] is "5" or "105") handles.Add(lines[i + 1]);
        Assert.Equal(handles.Count, handles.Distinct().Count());
        var seed = Convert.ToInt32(lines[Array.IndexOf(lines, "$HANDSEED") + 2], 16);
        Assert.True(handles.All(h => Convert.ToInt32(h, 16) < seed));
    }

    [Fact]
    public void Dxf_R12_turns_splines_into_polylines()
    {
        var text = DxfWriter.ToString(Drawing(), version: DxfVersion.R12);
        Assert.Contains("AC1009", text);
        Assert.DoesNotContain("SPLINE", text);
        var back = RoundTrip(Drawing(), DxfVersion.R12);
        Assert.Empty(back.AllEntities().OfType<SplineEntity>());
        Assert.Equal(2, back.AllEntities().OfType<PolylineEntity>().Count());
    }

    [Fact]
    public void Spline_tolerance_controls_the_approximation()
    {
        // Without changing the global tolerance (other tests run in parallel).
        var s = SplineEditing.FromAnchors([new(0, 0, 0), new(10, 10, 0), new(20, 0, 0), new(30, 10, 0)], closed: false);
        var coarse = Nurbs.Flatten(s.Degree, s.ControlPoints, s.Weights, s.Knots, 0.1).Count;
        var fine = Nurbs.Flatten(s.Degree, s.ControlPoints, s.Weights, s.Knots, 0.0001).Count;
        Assert.True(fine > coarse, $"fine {fine} coarse {coarse}");
    }
}