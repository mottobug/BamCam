using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using BamCam.IO.Dxf;

namespace BamCam.Tests;

/// <summary>Single drawing objects hidden (right-click > Hide, View > Show All Objects).</summary>
public class HiddenEntityTests
{
    private static (CadDocument Doc, PolylineEntity Shown, PolylineEntity Hidden, SurfaceEntity Mesh) Drawing()
    {
        var doc = new CadDocument();
        var shown = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10)], true)));
        var hidden = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(100, 100), new(120, 100), new(120, 120)], true)));
        var mesh = new SurfaceEntity();
        mesh.Vertices.AddRange([new Point3(0, 0, 0), new Point3(1, 0, 0), new Point3(0, 1, 0)]);
        mesh.Faces.AddRange([0, 1, 2]);
        doc.AddEntity(mesh);
        hidden.Hidden = true;
        mesh.Hidden = true;
        return (doc, shown, hidden, mesh);
    }

    [Fact]
    public void Hidden_objects_stay_hidden_after_saving_and_opening()
    {
        var (doc, _, _, _) = Drawing();

        var xml = CbWriter.ToString(doc);
        var again = CbReader.Parse(xml);

        Assert.Equal([false, true, true], again.AllEntities().Select(e => e.Hidden));
        // One extra element that CamBam skips; the object itself is written as before.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "<BamCamHidden ?/>").Count);
    }

    [Fact]
    public void Showing_removes_the_mark_and_copies_keep_it()
    {
        var (_, _, hidden, _) = Drawing();

        Assert.True(hidden.CloneEntity().Hidden);
        hidden.Hidden = true; // twice: still one mark
        Assert.Single(hidden.Extensions);
        hidden.Hidden = false;
        Assert.False(hidden.Hidden);
        Assert.Empty(hidden.Extensions);
    }

    [Fact]
    public void Zoom_to_fit_and_the_visible_export_leave_hidden_objects_out()
    {
        var (doc, _, _, _) = Drawing();

        Assert.Equal(10, doc.Bounds().Max.X, 9);
        Assert.Equal(120, doc.Bounds(visibleOnly: false).Max.X, 9);
        var dxf = Path.Combine(Path.GetTempPath(), $"bamcam-hidden-{Guid.NewGuid():N}.dxf");
        try
        {
            DxfWriter.Save(doc, dxf, visibleOnly: true);
            var exported = DxfReader.Load(dxf, out _).AllEntities().ToList();
            Assert.Single(exported);
            Assert.Equal(10, exported[0].Bounds().Max.X, 6);
            DxfWriter.Save(doc, dxf, visibleOnly: false);
            Assert.Contains(DxfReader.Load(dxf, out _).AllEntities(), e => Math.Abs(e.Bounds().Max.X - 120) < 1e-6);
        }
        finally
        {
            File.Delete(dxf);
        }
    }
}
