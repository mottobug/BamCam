using System.Globalization;
using System.Text;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Stl;

/// <summary>Reads ASCII and binary STL meshes into a surface entity (vertices are merged).</summary>
public static class StlReader
{
    public static SurfaceEntity Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var surface = new SurfaceEntity();
        var index = new Dictionary<(long, long, long), int>();

        int Vertex(Point3 p)
        {
            var key = ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
            if (index.TryGetValue(key, out var i)) return i;
            i = surface.Vertices.Count;
            surface.Vertices.Add(p);
            index[key] = i;
            return i;
        }

        if (IsBinary(bytes))
        {
            var count = BitConverter.ToUInt32(bytes, 80);
            var offset = 84;
            for (var t = 0; t < count && offset + 50 <= bytes.Length; t++, offset += 50)
            {
                for (var v = 0; v < 3; v++)
                {
                    var o = offset + 12 + v * 12;
                    surface.Faces.Add(Vertex(new Point3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8))));
                }
            }
        }
        else
        {
            var tri = new List<int>(3);
            foreach (var raw in Encoding.ASCII.GetString(bytes).Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                tri.Add(Vertex(new Point3(D(parts[1]), D(parts[2]), D(parts[3]))));
                if (tri.Count == 3)
                {
                    surface.Faces.AddRange(tri);
                    tri.Clear();
                }
            }
        }
        return surface;
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 84) return false;
        var count = BitConverter.ToUInt32(bytes, 80);
        if (84 + (long)count * 50 == bytes.Length) return true;
        return !Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase);
    }
}
