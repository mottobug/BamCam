using System.Text;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Stl;

/// <summary>Writes a mesh as binary STL, in drawing units and with the transform of the object.</summary>
public static class StlWriter
{
    public static void Save(SurfaceEntity surface, string path)
    {
        using var stream = File.Create(path);
        Write(surface, stream);
    }

    /// <summary>Several meshes as one binary STL (File ▸ Export ▸ To STL).</summary>
    public static void Save(IEnumerable<SurfaceEntity> surfaces, string path)
    {
        using var stream = File.Create(path);
        Write(surfaces, stream);
    }

    public static void Write(SurfaceEntity surface, Stream stream) => Write([surface], stream);

    public static void Write(IEnumerable<SurfaceEntity> surfaces, Stream stream)
    {
        var meshes = surfaces.Select(s => (Vertices: s.WorldVertices().ToList(), s.Faces)).ToList();
        var triangles = meshes.Sum(m => m.Faces.Count / 3);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        // 80 bytes of header; it must not begin with "solid", which marks ASCII STL.
        var header = new byte[80];
        Encoding.ASCII.GetBytes("BamCam binary STL").CopyTo(header, 0);
        writer.Write(header);
        writer.Write((uint)triangles);
        foreach (var (vertices, faces) in meshes)
        for (var t = 0; t < faces.Count / 3; t++)
        {
            var (a, b, c) = (vertices[faces[3 * t]], vertices[faces[3 * t + 1]], vertices[faces[3 * t + 2]]);
            var normal = Point3.Cross(b - a, c - a);
            var length = normal.Length;
            normal = length > 0 ? new Point3(normal.X / length, normal.Y / length, normal.Z / length) : new Point3(0, 0, 0);
            foreach (var p in new[] { normal, a, b, c })
            {
                writer.Write((float)p.X);
                writer.Write((float)p.Y);
                writer.Write((float)p.Z);
            }
            writer.Write((ushort)0);
        }
    }
}
