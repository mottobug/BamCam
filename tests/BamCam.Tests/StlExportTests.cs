using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Stl;

namespace BamCam.Tests;

/// <summary>File ▸ Export ▸ To STL (issue 17): several surfaces in one binary STL, with their transforms.</summary>
public class StlExportTests
{
    private static SurfaceEntity Triangle(double z) => new()
    {
        Vertices = { new Point3(0, 0, z), new Point3(10, 0, z), new Point3(0, 10, z) },
        Faces = { 0, 1, 2 },
    };

    [Fact]
    public void Several_surfaces_go_into_one_file_in_drawing_coordinates()
    {
        var moved = Triangle(0);
        moved.Transform = Matrix4.Translation(100, 0);
        var path = Path.Combine(Path.GetTempPath(), "bamcam-stl-" + Guid.NewGuid().ToString("N") + ".stl");
        try
        {
            StlWriter.Save([Triangle(5), moved], path);

            Assert.Equal(84 + 2 * 50, new FileInfo(path).Length);
            var read = StlReader.Load(path);
            Assert.Equal(6, read.Faces.Count);
            var points = read.WorldVertices().ToList();
            Assert.Contains(points, p => Point3.Distance(p, new Point3(10, 0, 5)) < 1e-6);
            Assert.Contains(points, p => Point3.Distance(p, new Point3(110, 0, 0)) < 1e-6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
