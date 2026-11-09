using System.Globalization;
using System.Text;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Dxf;

/// <summary>DXF version written by <see cref="DxfWriter"/>.</summary>
public enum DxfVersion
{
    /// <summary>R12 (AC1009): read by almost every program; splines and texts become polylines.</summary>
    R12,
    /// <summary>2000 (AC1015): splines stay splines (SPLINE), polylines as LWPOLYLINE.</summary>
    R2000,
}

/// <summary>Writes DXF files: circles stay circles, point lists become points, splines (2000 only) splines,
/// everything else polylines with bulges.</summary>
public static class DxfWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Save(CadDocument doc, string path, bool visibleOnly = true, DxfVersion version = DxfVersion.R2000) =>
        File.WriteAllText(path, ToString(doc, visibleOnly, version), Encoding.ASCII);

    public static string ToString(CadDocument doc, bool visibleOnly = true, DxfVersion version = DxfVersion.R2000) =>
        version == DxfVersion.R12 ? R12(doc, visibleOnly) : R2000(doc, visibleOnly);

    private static string N(double v) => v.ToString("0.##########", Inv);

    // ------------------------------------------------------------------ R12

    private static string R12(CadDocument doc, bool visibleOnly)
    {
        var sb = new StringBuilder();
        void P(int code, string value) => sb.Append(code).Append("\r\n").Append(value).Append("\r\n");

        P(0, "SECTION"); P(2, "HEADER");
        P(9, "$ACADVER"); P(1, "AC1009");
        P(9, "$INSUNITS"); P(70, doc.Units == DrawingUnits.Inches ? "1" : "4");
        P(0, "ENDSEC");

        P(0, "SECTION"); P(2, "TABLES");
        P(0, "TABLE"); P(2, "LAYER"); P(70, doc.Layers.Count.ToString(Inv));
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
                if (visibleOnly && e.Hidden) continue;
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
                    P(70, ((pth.Closed ? 1 : 0) | (is3D ? 8 : 0)).ToString(Inv));
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
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 2000

    /// <summary>
    /// Minimal complete DXF 2000 file: header, the required tables (VPORT, LTYPE, LAYER, STYLE, VIEW, UCS, APPID,
    /// DIMSTYLE, BLOCK_RECORD), model and paper space blocks, entities with handles and owners, root dictionary.
    /// </summary>
    private static string R2000(CadDocument doc, bool visibleOnly)
    {
        var sb = new StringBuilder();
        var next = 0x20;
        string Handle() => (next++).ToString("X", Inv);
        void P(int code, string value) => sb.Append(code).Append("\r\n").Append(value).Append("\r\n");

        // Fixed handles of the structure.
        const string vportTable = "1", ltypeTable = "2", layerTable = "3", styleTable = "4", viewTable = "5", ucsTable = "6",
            appidTable = "7", dimstyleTable = "8", blockRecordTable = "9", modelRecord = "A", paperRecord = "B",
            rootDictionary = "C", groupDictionary = "D", continuous = "E", byBlock = "F", byLayer = "10";

        var body = new StringBuilder();
        var header = sb;
        sb = body;

        // ---- tables
        void Table(string name, string handle, int count, Action entries)
        {
            P(0, "TABLE"); P(2, name); P(5, handle); P(330, "0"); P(100, "AcDbSymbolTable"); P(70, count.ToString(Inv));
            if (name == "DIMSTYLE") P(100, "AcDbDimStyleTable");
            entries();
            P(0, "ENDTAB");
        }
        void Record(string type, string handle, string owner, string subclass)
        {
            P(0, type); P(type == "DIMSTYLE" ? 105 : 5, handle); P(330, owner);
            P(100, "AcDbSymbolTableRecord"); P(100, subclass);
        }

        P(0, "SECTION"); P(2, "CLASSES"); P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "TABLES");
        Table("VPORT", vportTable, 1, () =>
        {
            Record("VPORT", Handle(), vportTable, "AcDbViewportTableRecord");
            P(2, "*Active"); P(70, "0");
            P(10, "0"); P(20, "0"); P(11, "1"); P(21, "1"); P(12, "0"); P(22, "0");
            P(40, "100"); P(41, "1.5");
        });
        Table("LTYPE", ltypeTable, 3, () =>
        {
            foreach (var (name, handle) in new[] { ("ByBlock", byBlock), ("ByLayer", byLayer), ("Continuous", continuous) })
            {
                Record("LTYPE", handle, ltypeTable, "AcDbLinetypeTableRecord");
                P(2, name); P(70, "0"); P(3, name == "Continuous" ? "Solid line" : ""); P(72, "65"); P(73, "0"); P(40, "0");
            }
        });
        var layers = doc.Layers.Select(l => l.Name).Prepend("0").Distinct().ToList();
        Table("LAYER", layerTable, layers.Count, () =>
        {
            foreach (var name in layers)
            {
                var layer = doc.Layers.FirstOrDefault(l => l.Name == name);
                Record("LAYER", Handle(), layerTable, "AcDbLayerTableRecord");
                P(2, name); P(70, "0"); P(62, layer is { Visible: false } ? "-7" : "7"); P(6, "Continuous"); P(370, "-3");
            }
        });
        Table("STYLE", styleTable, 1, () =>
        {
            Record("STYLE", Handle(), styleTable, "AcDbTextStyleTableRecord");
            P(2, "Standard"); P(70, "0"); P(40, "0"); P(41, "1"); P(50, "0"); P(71, "0"); P(42, "2.5"); P(3, "txt"); P(4, "");
        });
        Table("VIEW", viewTable, 0, () => { });
        Table("UCS", ucsTable, 0, () => { });
        Table("APPID", appidTable, 1, () =>
        {
            Record("APPID", Handle(), appidTable, "AcDbRegAppTableRecord");
            P(2, "ACAD"); P(70, "0");
        });
        Table("DIMSTYLE", dimstyleTable, 1, () =>
        {
            Record("DIMSTYLE", Handle(), dimstyleTable, "AcDbDimStyleTableRecord");
            P(2, "Standard"); P(70, "0");
        });
        Table("BLOCK_RECORD", blockRecordTable, 2, () =>
        {
            foreach (var (name, handle) in new[] { ("*Model_Space", modelRecord), ("*Paper_Space", paperRecord) })
            {
                Record("BLOCK_RECORD", handle, blockRecordTable, "AcDbBlockTableRecord");
                P(2, name);
            }
        });
        P(0, "ENDSEC");

        // ---- blocks
        P(0, "SECTION"); P(2, "BLOCKS");
        foreach (var (name, record) in new[] { ("*Model_Space", modelRecord), ("*Paper_Space", paperRecord) })
        {
            P(0, "BLOCK"); P(5, Handle()); P(330, record); P(100, "AcDbEntity"); P(8, "0"); P(100, "AcDbBlockBegin");
            P(2, name); P(70, "0"); P(10, "0"); P(20, "0"); P(30, "0"); P(3, name); P(1, "");
            P(0, "ENDBLK"); P(5, Handle()); P(330, record); P(100, "AcDbEntity"); P(8, "0"); P(100, "AcDbBlockEnd");
        }
        P(0, "ENDSEC");

        // ---- entities
        void Entity(string type, string layer, string subclass)
        {
            P(0, type); P(5, Handle()); P(330, modelRecord); P(100, "AcDbEntity"); P(8, layer); P(100, subclass);
        }
        P(0, "SECTION"); P(2, "ENTITIES");
        foreach (var layer in doc.Layers)
        {
            if (visibleOnly && !layer.Visible) continue;
            foreach (var e in layer.Entities)
            {
                if (visibleOnly && e.Hidden) continue;
                switch (e)
                {
                    case CircleEntity c when c.Transform.IsIdentity:
                        Entity("CIRCLE", layer.Name, "AcDbCircle");
                        P(10, N(c.Center.X)); P(20, N(c.Center.Y)); P(30, N(c.Center.Z)); P(40, N(c.Radius));
                        continue;
                    case PointListEntity pl:
                        foreach (var p in pl.WorldPoints())
                        {
                            Entity("POINT", layer.Name, "AcDbPoint");
                            P(10, N(p.X)); P(20, N(p.Y)); P(30, N(p.Z));
                        }
                        continue;
                    case SplineEntity s:
                    {
                        var t = s.Transform;
                        var ctrl = s.ControlPoints.Select(p => t.IsIdentity ? p : t.Transform(p)).ToList();
                        var planar = ctrl.All(p => Math.Abs(p.Z - ctrl[0].Z) < 1e-9);
                        var rational = s.Weights.Count == ctrl.Count;
                        Entity("SPLINE", layer.Name, "AcDbSpline");
                        if (planar) { P(210, "0"); P(220, "0"); P(230, "1"); }
                        P(70, ((s.Closed ? 1 : 0) | (rational ? 4 : 0) | (planar ? 8 : 0)).ToString(Inv));
                        P(71, s.Degree.ToString(Inv)); P(72, s.Knots.Count.ToString(Inv)); P(73, ctrl.Count.ToString(Inv)); P(74, "0");
                        P(42, "0.0000001"); P(43, "0.0000001");
                        foreach (var k in s.Knots) P(40, N(k));
                        foreach (var p in ctrl) { P(10, N(p.X)); P(20, N(p.Y)); P(30, N(p.Z)); }
                        if (rational) foreach (var w in s.Weights) P(41, N(w));
                        continue;
                    }
                }
                foreach (var pth in e.ToPaths())
                {
                    if (pth.Count < 2) continue;
                    var z = pth.Vertices[0].Point.Z;
                    if (pth.Vertices.Any(v => Math.Abs(v.Point.Z - z) > 1e-9))
                    {
                        // 3D paths: straight lines between the vertices.
                        foreach (var s in pth.Segments())
                        {
                            Entity("LINE", layer.Name, "AcDbLine");
                            P(10, N(s.Start.X)); P(20, N(s.Start.Y)); P(30, N(s.Start.Z));
                            P(11, N(s.End.X)); P(21, N(s.End.Y)); P(31, N(s.End.Z));
                        }
                        continue;
                    }
                    Entity("LWPOLYLINE", layer.Name, "AcDbPolyline");
                    P(90, pth.Count.ToString(Inv)); P(70, pth.Closed ? "1" : "0");
                    if (Math.Abs(z) > 1e-12) P(38, N(z));
                    foreach (var v in pth.Vertices)
                    {
                        P(10, N(v.Point.X)); P(20, N(v.Point.Y));
                        if (Math.Abs(v.Bulge) > 1e-12) P(42, N(v.Bulge));
                    }
                }
            }
        }
        P(0, "ENDSEC");

        // ---- objects
        P(0, "SECTION"); P(2, "OBJECTS");
        P(0, "DICTIONARY"); P(5, rootDictionary); P(330, "0"); P(100, "AcDbDictionary"); P(281, "1");
        P(3, "ACAD_GROUP"); P(350, groupDictionary);
        P(0, "DICTIONARY"); P(5, groupDictionary); P(330, rootDictionary); P(100, "AcDbDictionary"); P(281, "1");
        P(0, "ENDSEC");
        P(0, "EOF");

        // Header last: it needs the next free handle.
        sb = header;
        P(0, "SECTION"); P(2, "HEADER");
        P(9, "$ACADVER"); P(1, "AC1015");
        P(9, "$HANDSEED"); P(5, next.ToString("X", Inv));
        P(9, "$INSUNITS"); P(70, doc.Units == DrawingUnits.Inches ? "1" : "4");
        P(9, "$MEASUREMENT"); P(70, doc.Units == DrawingUnits.Inches ? "0" : "1");
        P(0, "ENDSEC");
        return header.Append(body).ToString();
    }
}
