using System.Globalization;
using System.Text;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Dxf;

/// <summary>Writes DXF R12 (AC1009): circles stay circles, everything else becomes polylines with bulges.</summary>
public static class DxfWriter
{
    public static void Save(CadDocument doc, string path, bool visibleOnly = true)
    {
        var sb = new StringBuilder();
        void P(int code, string value) => sb.Append(code).Append("\r\n").Append(value).Append("\r\n");
        string N(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

        P(0, "SECTION"); P(2, "HEADER");
        P(9, "$ACADVER"); P(1, "AC1009");
        P(9, "$INSUNITS"); P(70, doc.Units == DrawingUnits.Inches ? "1" : "4");
        P(0, "ENDSEC");

        P(0, "SECTION"); P(2, "TABLES");
        P(0, "TABLE"); P(2, "LAYER"); P(70, doc.Layers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var l in doc.Layers)
        {
            P(0, "LAYER"); P(2, l.Name); P(70, "0"); P(62, l.Visible ? "7" : "-7"); P(6, "CONTINUOUS");
        }
        P(0, "ENDTAB");
        P(0, "ENDSEC");

        P(0, "SECTION"); P(2, "ENTITIES");
        foreach (var layer in doc.Layers)
        {
            if (visibleOnly && !layer.Visible) continue;
            foreach (var e in layer.Entities)
            {
                if (e is CircleEntity c && c.Transform.IsIdentity)
                {
                    P(0, "CIRCLE"); P(8, layer.Name);
                    P(10, N(c.Center.X)); P(20, N(c.Center.Y)); P(30, N(c.Center.Z)); P(40, N(c.Radius));
                    continue;
                }
                if (e is PointListEntity pl)
                {
                    foreach (var p in pl.WorldPoints())
                    {
                        P(0, "POINT"); P(8, layer.Name); P(10, N(p.X)); P(20, N(p.Y)); P(30, N(p.Z));
                    }
                    continue;
                }
                foreach (var pth in e.ToPaths())
                {
                    if (pth.Count < 2) continue;
                    var is3D = pth.Vertices.Any(v => Math.Abs(v.Point.Z - pth.Vertices[0].Point.Z) > 1e-9);
                    P(0, "POLYLINE"); P(8, layer.Name); P(66, "1");
                    P(10, "0"); P(20, "0"); P(30, is3D ? "0" : N(pth.Vertices[0].Point.Z));
                    P(70, ((pth.Closed ? 1 : 0) | (is3D ? 8 : 0)).ToString(CultureInfo.InvariantCulture));
                    foreach (var v in pth.Vertices)
                    {
                        P(0, "VERTEX"); P(8, layer.Name);
                        P(10, N(v.Point.X)); P(20, N(v.Point.Y)); P(30, N(v.Point.Z));
                        if (!is3D && Math.Abs(v.Bulge) > 1e-12) P(42, N(v.Bulge));
                        if (is3D) P(70, "32");
                    }
                    P(0, "SEQEND"); P(8, layer.Name);
                }
            }
        }
        P(0, "ENDSEC");
        P(0, "EOF");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }
}
