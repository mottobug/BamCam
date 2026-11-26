// Demo 8: a file format. XYZ point lists (one point per line: "x y" or "x y z") in File ▸ Open and File ▸ Export.
//
// - host.AddImporter: reads a file into a new drawing (it replaces the open one, as opening a .cb file does).
// - host.AddExporter: writes the open drawing to a file.
// Read numbers with the invariant culture, so that a file reads the same on every computer.

using System.Globalization;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace BamCam.Example;

internal static class Demo8_FileFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Register(IPluginHost host, bool german)
    {
        var xyz = new FileFormat(german ? "XYZ-Punktliste" : "XYZ point list", ".xyz");
        host.AddImporter(xyz, Read);
        host.AddExporter(xyz, Write);
    }

    /// <summary>A drawing with one point list; lines that are no point (e.g. "# comment") are skipped.</summary>
    private static CadDocument Read(string path)
    {
        var doc = new CadDocument();
        var list = new PointListEntity();
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || line.TrimStart().StartsWith('#')) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, Inv, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, Inv, out var y)) continue;
            var z = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, Inv, out var zz) ? zz : 0;
            list.Points.Add(new Point3(x, y, z));
        }
        doc.AddEntity(list);
        return doc;
    }

    /// <summary>The points of all point lists and the vertices of all other objects.</summary>
    private static void Write(CadDocument doc, string path)
    {
        var lines = new List<string> { "# x y z" };
        foreach (var e in doc.AllEntities())
        {
            var points = e is PointListEntity pl ? pl.WorldPoints() : e.ToPaths().SelectMany(p => p.Vertices.Select(v => v.Point));
            lines.AddRange(points.Select(p => string.Format(Inv, "{0} {1} {2}", p.X, p.Y, p.Z)));
        }
        File.WriteAllLines(path, lines);
    }
}
