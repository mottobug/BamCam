using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.IO.Cb;

/// <summary>Reads CamBam-compatible .cb project files.</summary>
public static class CbReader
{
    public static CadDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var doc = Load(stream);
        doc.FilePath = path;
        if (string.IsNullOrEmpty(doc.Name)) doc.Name = Path.GetFileNameWithoutExtension(path);
        return doc;
    }

    public static CadDocument Load(Stream stream) => Read(XDocument.Load(stream, LoadOptions.PreserveWhitespace));

    public static CadDocument Parse(string xml) => Read(XDocument.Parse(xml, LoadOptions.PreserveWhitespace));

    public static CadDocument Read(XDocument xdoc)
    {
        var root = xdoc.Root ?? throw new InvalidDataException(Tr.T("Empty document"));
        if (root.Name.LocalName != "CADFile") throw new InvalidDataException(Tr.F("Not a CamBam drawing (root '{0}').", root.Name.LocalName));

        var doc = new CadDocument
        {
            Name = (string?)root.Attribute("Name") ?? "",
            FormatVersion = (string?)root.Attribute("Version") ?? "0.9.8.0",
        };
        if (root.Attribute("units") is { } u && CbFormat.TryParseEnum(typeof(DrawingUnits), u.Value, out var units))
            doc.Units = (DrawingUnits)units;

        foreach (var node in root.Nodes())
        {
            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value)) doc.Script = (doc.Script ?? "") + text.Value;
                continue;
            }
            if (node is not XElement el) continue;
            switch (el.Name.LocalName)
            {
                case "layers":
                    foreach (var l in el.Elements("layer")) ReadLayer(doc, l);
                    break;
                case "MachiningOptions":
                    ReadMachining(doc.Machining, el);
                    break;
                case "parts":
                    foreach (var p in el.Elements("part")) doc.Parts.Add(ReadPart(p));
                    break;
                case "ShowCutWidths": doc.ShowCutWidths = CbFormat.ParseBool(el.Value); break;
                case "ShowDirectionVector": doc.ShowDirectionVector = CbFormat.ParseBool(el.Value, true); break;
                case "ShowRapids": doc.ShowRapids = CbFormat.ParseBool(el.Value, true); break;
                case "ShowStock": doc.ShowStock = CbFormat.ParseBool(el.Value, true); break;
                case "ToolpathVisibility":
                case "ToolpathVisiblility":
                    doc.ToolpathVisibility = el.Value.Replace(" ", "").Equals("SelectedOnly", StringComparison.OrdinalIgnoreCase)
                        ? ToolpathVisibility.SelectedOnly : ToolpathVisibility.All;
                    break;
                case "ActiveLayer": doc.ActiveLayerName = el.Value; break;
                case "ActivePart": doc.ActivePartName = el.Value; break;
                default: doc.Extensions.Add(new XElement(el)); break;
            }
        }
        if (doc.Layers.Count == 0) doc.Layers.Add(new Layer("Default"));
        doc.IsModified = false;
        return doc;
    }

    private static void ReadLayer(CadDocument doc, XElement el)
    {
        var layer = new Layer((string?)el.Attribute("name") ?? "Layer")
        {
            Color = CbFormat.ParseColor((string?)el.Attribute("color"), RgbColor.DefaultLayer),
            PenWidth = el.Attribute("pen") is { } pen ? CbFormat.ParseDouble(pen.Value, 1) : 1,
            Visible = el.Attribute("visible") is not { } vis || CbFormat.ParseBool(vis.Value, true),
            Alpha = el.Attribute("alpha") is { } a ? CbFormat.ParseDouble(a.Value, 1) : 1,
            Locked = el.Attribute("locked") is { } lk && CbFormat.ParseBool(lk.Value),
        };
        doc.Layers.Add(layer);
        foreach (var node in el.Nodes())
        {
            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value)) layer.Script = (layer.Script ?? "") + text.Value;
                continue;
            }
            if (node is not XElement child) continue;
            switch (child.Name.LocalName)
            {
                case "ModificationCount": layer.ModificationCount = CbFormat.ParseInt(child.Value); break;
                case "Tag": layer.Tag = child.Value; break;
                case "objects":
                    foreach (var o in child.Elements())
                        if (ReadEntity(o) is { } e) doc.AddLoadedEntity(e, layer);
                    break;
                default: layer.Extensions.Add(new XElement(child)); break;
            }
        }
    }

    private static readonly HashSet<string> CommonEntityChildren = ["ModificationCount", "mat", "Tag"];

    private static Entity? ReadEntity(XElement el)
    {
        Entity e;
        var known = new HashSet<string>(CommonEntityChildren);
        switch (el.Name.LocalName)
        {
            case "pline":
            {
                var p = new PolylineEntity { Closed = CbFormat.ParseBool((string?)el.Attribute("Closed")) };
                foreach (var v in el.Element("pts")?.Elements("p") ?? [])
                    p.Points.Add(new Vertex(CbFormat.ParsePoint3(v.Value), CbFormat.ParseDouble((string?)v.Attribute("b"))));
                known.Add("pts");
                e = p;
                break;
            }
            case "circle":
                e = new CircleEntity
                {
                    Center = CbFormat.ParsePoint3((string?)el.Attribute("c")),
                    Diameter = CbFormat.ParseDouble((string?)el.Attribute("d")),
                };
                break;
            case "rect":
                e = new RectEntity
                {
                    LowerLeft = CbFormat.ParsePoint3((string?)el.Attribute("p")),
                    Width = CbFormat.ParseDouble((string?)el.Attribute("w")),
                    Height = CbFormat.ParseDouble((string?)el.Attribute("h")),
                    CornerRadius = CbFormat.ParseDouble((string?)el.Attribute("radius")),
                };
                known.Add("pts"); // redundant outline, regenerated on save
                break;
            case "points":
            {
                var p = new PointListEntity();
                foreach (var v in el.Element("pts")?.Elements("p") ?? []) p.Points.Add(CbFormat.ParsePoint3(v.Value));
                known.Add("pts");
                e = p;
                break;
            }
            case "line":
            {
                var l = new LineEntity();
                foreach (var v in el.Element("pts")?.Elements("p") ?? []) l.Points.Add(CbFormat.ParsePoint3(v.Value));
                known.Add("pts");
                e = l;
                break;
            }
            case "arc":
            {
                // <arc p="cx,cy,cz" r="radius" s="start°" w="sweep°">; the start angle may exceed 360.
                e = new ArcEntity
                {
                    Center = CbFormat.ParsePoint3((string?)el.Attribute("p") ?? (string?)el.Attribute("c")),
                    Radius = CbFormat.ParseDouble((string?)el.Attribute("r")),
                    StartAngle = CbFormat.ParseDouble((string?)el.Attribute("s")),
                    Sweep = CbFormat.ParseDouble((string?)el.Attribute("w")),
                };
                break;
            }
            case "text":
            {
                var t = new TextEntity
                {
                    Position = CbFormat.ParsePoint3((string?)el.Attribute("p1")),
                    P2 = (string?)el.Attribute("p2"),
                    Height = CbFormat.ParseDouble((string?)el.Attribute("Height"), 10),
                    Font = (string?)el.Attribute("Font") ?? "",
                    // Mixed content: skip indentation-only text nodes between child elements.
                    Text = string.Concat(el.Nodes().OfType<XText>().Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Value)),
                };
                var style = ((string?)el.Attribute("style") ?? "").ToLowerInvariant();
                t.Bold = style.Contains("bold");
                t.Italic = style.Contains("italic");
                var align = ((string?)el.Attribute("align") ?? "bottom,left").Split(',');
                if (align.Length > 0 && CbFormat.TryParseEnum(typeof(TextVerticalAlignment), align[0], out var va))
                    t.VerticalAlignment = (TextVerticalAlignment)va;
                if (align.Length > 1 && CbFormat.TryParseEnum(typeof(TextAlignment), align[1], out var ha))
                    t.HorizontalAlignment = (TextAlignment)ha;
                e = t;
                break;
            }
            case "surface":
            {
                var s = new SurfaceEntity();
                foreach (var v in el.Element("verts")?.Elements("v") ?? []) s.Vertices.Add(CbFormat.ParsePoint3(v.Value));
                foreach (var f in el.Element("faces")?.Elements("f") ?? [])
                {
                    var idx = f.Value.Split(',');
                    if (idx.Length < 3) continue;
                    s.Faces.Add(CbFormat.ParseInt(idx[0]));
                    s.Faces.Add(CbFormat.ParseInt(idx[1]));
                    s.Faces.Add(CbFormat.ParseInt(idx[2]));
                }
                known.Add("verts");
                known.Add("faces");
                e = s;
                break;
            }
            default:
                e = new UnknownEntity(new XElement(el));
                e.Id = CbFormat.ParseInt((string?)el.Attribute("id"));
                return e;
        }

        e.Id = CbFormat.ParseInt((string?)el.Attribute("id"));
        e.Transform = CbFormat.ParseMatrix((string?)el.Element("mat")?.Attribute("m"));
        e.Tag = el.Element("Tag")?.Value;
        foreach (var child in el.Elements())
            if (!known.Contains(child.Name.LocalName)) e.Extensions.Add(new XElement(child));
        return e;
    }

    private static void ReadStock(Stock stock, XElement el)
    {
        stock.Material = el.Element("Material")?.Value ?? "";
        stock.PMin = CbFormat.ParsePoint3(el.Element("PMin")?.Value);
        stock.PMax = CbFormat.ParsePoint3(el.Element("PMax")?.Value);
        stock.Color = CbFormat.ParseColor(el.Element("Color")?.Value, stock.Color);
    }

    private static void ReadMachining(MachiningOptions m, XElement el)
    {
        m.FastPlungeHeight = -1; // absent = default: one minor grid unit
        foreach (var c in el.Elements())
        {
            var v = c.Value;
            switch (c.Name.LocalName)
            {
                case "OutFile": m.OutFile = v; break;
                case "RebuildToolpathBeforePost":
                    if (CbFormat.TryParseEnum(typeof(RebuildToolpath), v, out var rb)) m.RebuildToolpathBeforePost = (RebuildToolpath)rb;
                    break;
                case "FastPlungeHeight": m.FastPlungeHeight = CbFormat.ParseDouble(v, -1); break;
                case "PostProcessor": m.PostProcessor = v; break;
                case "PostProcessorMacros": m.PostProcessorMacros = v; break;
                case "CustomFileHeader": m.CustomFileHeader = v; break;
                case "CustomFileFooter": m.CustomFileFooter = v; break;
                case "Stock": ReadStock(m.Stock, c); break;
                case "Style": m.Style = v; break;
                case "StyleLibrary": m.StyleLibrary = v; break;
                case "ToolDiameter": m.ToolDiameter = CbFormat.ParseDouble(v); break;
                case "ToolLibrary": m.ToolLibrary = v; break;
                case "ToolNumber": m.ToolNumber = CbFormat.ParseInt(v); break;
                case "ToolProfile":
                    if (CbFormat.TryParseEnum(typeof(ToolProfile), v, out var tp)) m.ToolProfile = (ToolProfile)tp;
                    break;
                case "MachiningOrigin": m.MachiningOrigin = string.IsNullOrWhiteSpace(v) ? OptionalPoint.None : new OptionalPoint(CbFormat.ParsePoint3(v)); break;
                case "VelocityMode":
                    if (CbFormat.TryParseEnum(typeof(VelocityMode), v, out var vm)) m.VelocityMode = (VelocityMode)vm;
                    break;
                case "ArcCenterMode":
                    if (CbFormat.TryParseEnum(typeof(ArcCenterMode), v, out var acm)) m.ArcCenterMode = (ArcCenterMode)acm;
                    break;
                default: m.Extensions.Add(new XElement(c)); break;
            }
        }
    }

    private static Part ReadPart(XElement el)
    {
        var part = new Part { Name = (string?)el.Attribute("Name") ?? "Part" };
        foreach (var c in el.Elements())
        {
            var v = c.Value;
            switch (c.Name.LocalName)
            {
                case "machineops":
                    foreach (var m in c.Elements()) part.Operations.Add(ReadOp(m));
                    break;
                case "Stock": ReadStock(part.Stock, c); break;
                case "ToolProfile":
                    if (CbFormat.TryParseEnum(typeof(ToolProfile), v, out var tp)) part.ToolProfile = (ToolProfile)tp;
                    break;
                case "MachiningOrigin": part.MachiningOrigin = string.IsNullOrWhiteSpace(v) ? OptionalPoint.None : new OptionalPoint(CbFormat.ParsePoint3(v)); break;
                case "Enabled": part.Enabled = CbFormat.ParseBool(v, true); break;
                case "OutFile": part.OutFile = v; break;
                case "Style": part.Style = v; break;
                case "StyleLibrary": part.StyleLibrary = v; break;
                case "ToolLibrary": part.ToolLibrary = v; break;
                case "ToolNumber": part.ToolNumber = CbFormat.ParseInt(v); break;
                case "ToolDiameter": part.ToolDiameter = CbFormat.ParseDouble(v); break;
                case "Tag": part.Tag = v; break;
                case "Nesting": ReadNesting(part.Nesting, c); break;
                case "NestingItem":
                    foreach (var item in c.Elements("NestingItem"))
                        part.NestingItems.Add(new NestingItem
                        {
                            Location = CbFormat.ParsePoint2(item.Element("Location")?.Value),
                            Transform = CbFormat.ParseMatrix((string?)item.Element("Transform")?.Attribute("m")),
                        });
                    break;
                default: part.Extensions.Add(new XElement(c)); break;
            }
        }
        return part;
    }

    private static void ReadNesting(NestingSettings n, XElement el)
    {
        foreach (var c in el.Elements())
        {
            var v = c.Value;
            switch (c.Name.LocalName)
            {
                case "BasePoint": n.BasePoint = CbFormat.ParsePoint2(v); break;
                case "NestMethod":
                    if (CbFormat.TryParseEnum(typeof(NestMethod), v, out var nm)) n.NestMethod = (NestMethod)nm;
                    break;
                case "Rows": n.Rows = CbFormat.ParseInt(v, 1); break;
                case "Columns": n.Columns = CbFormat.ParseInt(v, 1); break;
                case "Spacing": n.Spacing = CbFormat.ParseDouble(v); break;
                case "GridOrder" when CbFormat.TryParseEnum(typeof(NestGridOrder), v, out var go):
                    n.GridOrder = (NestGridOrder)go;
                    break;
                case "GridAlternate": n.GridAlternate = !string.Equals(v.Trim(), "false", StringComparison.OrdinalIgnoreCase); break;
                case "PointListID": n.PointListID = CbFormat.ParseInt(v); break;
                default: n.Extensions.Add(new XElement(c)); break;
            }
        }
    }

    private static MachineOp ReadOp(XElement el)
    {
        var name = el.Name.LocalName;
        var op = MachineOpFactory.Create(name);
        if (op == null)
        {
            var xsiType = el.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value;
            var un = new UnsupportedOp(xsiType ?? name) { RawXml = new XElement(el) };
            un.Name = el.Element("Name")?.Value ?? un.XmlName;
            un.Enabled = el.Element("Enabled") is not { } en || CbFormat.ParseBool(en.Value, true);
            foreach (var prim in el.Element("primitive")?.Elements("prim") ?? []) un.PrimitiveIds.Add(CbFormat.ParseInt(prim.Value));
            return un;
        }

        var parameters = op.Parameters().ToDictionary(p => p.Name, p => p.Param);
        foreach (var c in el.Elements())
        {
            var n = c.Name.LocalName;
            op.ElementOrder.Add(n);
            switch (n)
            {
                case "Name": op.Name = c.Value; break;
                case "Enabled": op.Enabled = CbFormat.ParseBool(c.Value, true); break;
                case "Style": op.Style = c.Value; break;
                case "Tag": op.Tag = c.Value; break;
                case "primitive":
                    foreach (var prim in c.Elements("prim")) op.PrimitiveIds.Add(CbFormat.ParseInt(prim.Value));
                    break;
                case "BoundaryShapes" when op is Profile3DOp p3:
                    foreach (var id in c.Elements("id")) p3.BoundaryShapes.Add(CbFormat.ParseInt(id.Value));
                    break;
                case "Tabs" when op is ProfileOp profile:
                    foreach (var t in c.Elements("HoldingTab"))
                    {
                        profile.Tabs.Add(new HoldingTab
                        {
                            ParentEntityID = CbFormat.ParseInt(t.Element("ParentEntityID")?.Value),
                            ParametricPoint = CbFormat.ParseDouble(t.Element("ParametricPoint")?.Value),
                            NormalInverted = CbFormat.ParseBool(t.Element("NormalInverted")?.Value),
                            Normal = new Point2(
                                CbFormat.ParseDouble(t.Element("Normal")?.Element("X")?.Value),
                                CbFormat.ParseDouble(t.Element("Normal")?.Element("Y")?.Value)),
                        });
                    }
                    break;
                default:
                    if (parameters.TryGetValue(n, out var param)) CbParams.Read(param, c);
                    else op.Extensions.Add(new XElement(c));
                    break;
            }
        }
        return op;
    }
}
