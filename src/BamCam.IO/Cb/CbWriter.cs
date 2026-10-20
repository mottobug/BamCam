using System.Text;
using System.Xml;
using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Cb;

/// <summary>Writes CamBam-compatible .cb project files.</summary>
public static class CbWriter
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    public static void Save(CadDocument doc, string path, int backups = 0)
    {
        if (backups > 0 && File.Exists(path)) RotateBackups(path, backups);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) Save(doc, fs);
        File.Move(tmp, path, overwrite: true);
        doc.FilePath = path;
        doc.IsModified = false;
    }

    /// <summary>Keeps <c>name.b1</c> (newest) … <c>name.bN</c>.</summary>
    private static void RotateBackups(string path, int count)
    {
        for (var i = count - 1; i >= 1; i--)
        {
            var src = $"{path}.b{i}";
            if (File.Exists(src)) File.Move(src, $"{path}.b{i + 1}", overwrite: true);
        }
        File.Copy(path, $"{path}.b1", overwrite: true);
    }

    public static void Save(CadDocument doc, Stream stream)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            Encoding = new UTF8Encoding(false),
        };
        using var writer = XmlWriter.Create(stream, settings);
        ToXml(doc).Save(writer);
    }

    public static string ToString(CadDocument doc)
    {
        using var ms = new MemoryStream();
        Save(doc, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static XDocument ToXml(CadDocument doc)
    {
        var root = new XElement("CADFile",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsd", Xsd.NamespaceName),
            new XAttribute("Version", doc.FormatVersion),
            new XAttribute("Name", doc.Name));
        if (doc.Units != DrawingUnits.Millimeters) root.Add(new XAttribute("units", doc.Units.ToString()));

        var nextId = doc.AllEntities().Select(e => e.Id).DefaultIfEmpty(0).Max() + 1;
        root.Add(new XElement("layers", doc.Layers.Select(l => WriteLayer(l, ref nextId))));
        root.Add(WriteMachining(doc.Machining));
        root.Add(new XElement("parts", doc.Parts.Select(WritePart)));

        if (doc.ShowCutWidths) root.Add(new XElement("ShowCutWidths", "true"));
        if (!doc.ShowDirectionVector) root.Add(new XElement("ShowDirectionVector", "false"));
        if (!doc.ShowRapids) root.Add(new XElement("ShowRapids", "false"));
        if (!doc.ShowStock) root.Add(new XElement("ShowStock", "false"));
        if (doc.ToolpathVisibility == ToolpathVisibility.SelectedOnly) root.Add(new XElement("ToolpathVisibility", "SelectedOnly"));
        foreach (var x in doc.Extensions) root.Add(new XElement(x));
        if (!string.IsNullOrEmpty(doc.Script)) root.Add(new XText(doc.Script));
        root.Add(new XElement("ActiveLayer", doc.ActiveLayer.Name));
        if (doc.Parts.Count > 0) root.Add(new XElement("ActivePart", doc.ActivePart.Name));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement WriteLayer(Layer layer, ref int nextId)
    {
        var el = new XElement("layer",
            new XAttribute("name", layer.Name),
            new XAttribute("color", CbFormat.Color(layer.Color)));
        if (Math.Abs(layer.PenWidth - 1) > 1e-9) el.Add(new XAttribute("pen", CbFormat.Double(layer.PenWidth)));
        if (!layer.Visible) el.Add(new XAttribute("visible", "false"));
        if (layer.Alpha < 1 - 1e-9) el.Add(new XAttribute("alpha", CbFormat.Double(layer.Alpha)));
        if (layer.Locked) el.Add(new XAttribute("locked", "true"));

        el.Add(new XElement("ModificationCount", CbFormat.Int(layer.ModificationCount)));
        var objects = new XElement("objects");
        foreach (var e in layer.Entities)
            foreach (var x in WriteEntity(e, ref nextId)) objects.Add(x);
        el.Add(objects);
        if (!string.IsNullOrEmpty(layer.Tag)) el.Add(new XElement("Tag", layer.Tag));
        foreach (var x in layer.Extensions) el.Add(new XElement(x));
        if (!string.IsNullOrEmpty(layer.Script)) el.Add(new XText(layer.Script));
        return el;
    }

    private static XElement Mat(Matrix4 m) => new("mat", new XAttribute("m", CbFormat.Matrix(m)));

    private static void AddCommon(XElement el, Entity e)
    {
        if (!string.IsNullOrEmpty(e.Tag)) el.Add(new XElement("Tag", e.Tag));
        foreach (var x in e.Extensions) el.Add(new XElement(x));
    }

    private static XElement PolylineXml(int id, Path2 path, Matrix4 m, string element = "pline")
    {
        var el = new XElement(element, new XAttribute("id", id));
        if (path.Closed) el.Add(new XAttribute("Closed", "true"));
        el.Add(Mat(m));
        el.Add(new XElement("pts", path.Vertices.Select(v =>
        {
            var p = new XElement("p", CbFormat.Pt3(v.Point));
            if (Math.Abs(v.Bulge) > 1e-15) p.Add(new XAttribute("b", CbFormat.Double(v.Bulge)));
            return p;
        })));
        return el;
    }

    private static IEnumerable<XElement> WriteEntity(Entity e, ref int nextId)
    {
        switch (e)
        {
            case PolylineEntity p:
            {
                var el = PolylineXml(p.Id, p.LocalPath, p.Transform);
                AddCommon(el, e);
                return [el];
            }
            case CircleEntity c:
            {
                var el = new XElement("circle", new XAttribute("id", c.Id));
                if (c.Center != Point3.Zero) el.Add(new XAttribute("c", CbFormat.Pt3(c.Center)));
                el.Add(new XAttribute("d", CbFormat.Double(c.Diameter)), Mat(c.Transform));
                AddCommon(el, e);
                return [el];
            }
            case RectEntity r:
            {
                var el = new XElement("rect", new XAttribute("id", r.Id), new XAttribute("Closed", "true"));
                if (r.LowerLeft != Point3.Zero) el.Add(new XAttribute("p", CbFormat.Pt3(r.LowerLeft)));
                el.Add(new XAttribute("w", CbFormat.Double(r.Width)), new XAttribute("h", CbFormat.Double(r.Height)));
                if (r.CornerRadius > 0) el.Add(new XAttribute("radius", CbFormat.Double(r.CornerRadius)));
                el.Add(Mat(r.Transform));
                el.Add(new XElement("pts", r.LocalPath.Vertices.Select(v =>
                {
                    var p = new XElement("p", CbFormat.Pt3(v.Point));
                    if (Math.Abs(v.Bulge) > 1e-15) p.Add(new XAttribute("b", CbFormat.Double(v.Bulge)));
                    return p;
                })));
                AddCommon(el, e);
                return [el];
            }
            case PointListEntity pl:
            {
                var el = new XElement("points", new XAttribute("id", pl.Id), Mat(pl.Transform),
                    new XElement("pts", pl.Points.Select(p => new XElement("p", CbFormat.Pt3OmitZ(p)))));
                AddCommon(el, e);
                return [el];
            }
            case LineEntity l:
            {
                var el = new XElement("line", new XAttribute("id", l.Id), Mat(l.Transform),
                    new XElement("pts", l.Points.Select(p => new XElement("p", CbFormat.Pt3OmitZ(p)))));
                AddCommon(el, e);
                return [el];
            }
            case ArcEntity a:
            {
                // CamBam's own arc element has not been observed; a bulged polyline is read by every version.
                var path = a.ToPaths().First();
                var el = PolylineXml(a.Id, path.Transformed(InverseOrIdentity(a.Transform)), a.Transform);
                AddCommon(el, e);
                return [el];
            }
            case RegionEntity reg:
            {
                var result = new List<XElement> { PolylineXml(reg.Id, reg.Outer.LocalPath, reg.Transform) };
                foreach (var h in reg.Holes) result.Add(PolylineXml(nextId++, h.LocalPath, reg.Transform));
                return result;
            }
            case TextEntity t:
            {
                var el = new XElement("text", new XAttribute("id", t.Id),
                    new XAttribute("p1", CbFormat.Pt3(t.Position)));
                el.Add(new XAttribute("p2", t.P2 ?? CbFormat.Pt3(t.Position)));
                el.Add(new XAttribute("Height", CbFormat.Double(t.Height)));
                if (!string.IsNullOrEmpty(t.Font)) el.Add(new XAttribute("Font", t.Font));
                el.Add(new XAttribute("align", $"{t.VerticalAlignment.ToString().ToLowerInvariant()},{t.HorizontalAlignment.ToString().ToLowerInvariant()}"));
                var style = string.Join(",", new[] { t.Bold ? "bold" : null, t.Italic ? "italic" : null }.Where(s => s != null));
                if (style.Length > 0) el.Add(new XAttribute("style", style));
                el.Add(Mat(t.Transform));
                AddCommon(el, e);
                el.Add(new XText(t.Text));
                return [el];
            }
            case SurfaceEntity s:
            {
                var faces = new XElement("faces");
                for (var i = 0; i + 2 < s.Faces.Count; i += 3)
                    faces.Add(new XElement("f", $"{s.Faces[i]},{s.Faces[i + 1]},{s.Faces[i + 2]}"));
                var el = new XElement("surface", new XAttribute("id", s.Id), Mat(s.Transform), faces,
                    new XElement("verts", s.Vertices.Select(v => new XElement("v", CbFormat.Pt3(v)))));
                AddCommon(el, e);
                return [el];
            }
            case UnknownEntity u:
            {
                var el = new XElement(u.Xml);
                el.SetAttributeValue("id", u.Id);
                return [el];
            }
            default:
                return [];
        }
    }

    private static Matrix4 InverseOrIdentity(Matrix4 m) => m.IsIdentity ? Matrix4.Identity : Invert(m);

    private static Matrix4 Invert(Matrix4 m)
    {
        // Affine inverse (upper-left 3x3 + translation row).
        var a = m;
        var det = a.M11 * (a.M22 * a.M33 - a.M23 * a.M32) - a.M12 * (a.M21 * a.M33 - a.M23 * a.M31) + a.M13 * (a.M21 * a.M32 - a.M22 * a.M31);
        if (Math.Abs(det) < 1e-15) return Matrix4.Identity;
        var i11 = (a.M22 * a.M33 - a.M23 * a.M32) / det;
        var i12 = (a.M13 * a.M32 - a.M12 * a.M33) / det;
        var i13 = (a.M12 * a.M23 - a.M13 * a.M22) / det;
        var i21 = (a.M23 * a.M31 - a.M21 * a.M33) / det;
        var i22 = (a.M11 * a.M33 - a.M13 * a.M31) / det;
        var i23 = (a.M13 * a.M21 - a.M11 * a.M23) / det;
        var i31 = (a.M21 * a.M32 - a.M22 * a.M31) / det;
        var i32 = (a.M12 * a.M31 - a.M11 * a.M32) / det;
        var i33 = (a.M11 * a.M22 - a.M12 * a.M21) / det;
        var tx = -(a.M41 * i11 + a.M42 * i21 + a.M43 * i31);
        var ty = -(a.M41 * i12 + a.M42 * i22 + a.M43 * i32);
        var tz = -(a.M41 * i13 + a.M42 * i23 + a.M43 * i33);
        return new Matrix4(i11, i12, i13, 0, i21, i22, i23, 0, i31, i32, i33, 0, tx, ty, tz, 1);
    }

    private static XElement WriteStock(Stock s, bool withMaterial = true)
    {
        var el = new XElement("Stock");
        if (withMaterial && !string.IsNullOrEmpty(s.Material)) el.Add(new XElement("Material", s.Material));
        el.Add(new XElement("PMin", CbFormat.Pt3OmitZ(s.PMin)),
            new XElement("PMax", CbFormat.Pt3OmitZ(s.PMax)),
            new XElement("Color", CbFormat.Color(s.Color)));
        return el;
    }

    private static XElement WriteMachining(MachiningOptions m)
    {
        var el = new XElement("MachiningOptions");
        el.Add(new XElement("OutFile", m.OutFile));
        if (m.RebuildToolpathBeforePost != RebuildToolpath.IfNeeded)
            el.Add(new XElement("RebuildToolpathBeforePost", CbFormat.EnumText(m.RebuildToolpathBeforePost)));
        el.Add(new XElement("FastPlungeHeight", CbFormat.Double(m.FastPlungeHeight)));
        if (!string.IsNullOrEmpty(m.PostProcessor)) el.Add(new XElement("PostProcessor", m.PostProcessor));
        if (!string.IsNullOrEmpty(m.PostProcessorMacros)) el.Add(new XElement("PostProcessorMacros", m.PostProcessorMacros));
        if (!string.IsNullOrEmpty(m.CustomFileHeader)) el.Add(new XElement("CustomFileHeader", m.CustomFileHeader));
        if (!string.IsNullOrEmpty(m.CustomFileFooter)) el.Add(new XElement("CustomFileFooter", m.CustomFileFooter));
        el.Add(WriteStock(m.Stock));
        if (!string.IsNullOrEmpty(m.Style)) el.Add(new XElement("Style", m.Style));
        if (!string.IsNullOrEmpty(m.StyleLibrary)) el.Add(new XElement("StyleLibrary", m.StyleLibrary));
        el.Add(new XElement("ToolDiameter", CbFormat.Double(m.ToolDiameter)));
        if (!string.IsNullOrEmpty(m.ToolLibrary)) el.Add(new XElement("ToolLibrary", m.ToolLibrary));
        el.Add(new XElement("ToolNumber", CbFormat.Int(m.ToolNumber)));
        el.Add(new XElement("ToolProfile", CbFormat.EnumText(m.ToolProfile)));
        if (m.VelocityMode != VelocityMode.Default) el.Add(new XElement("VelocityMode", CbFormat.EnumText(m.VelocityMode)));
        if (m.ArcCenterMode != ArcCenterMode.Default) el.Add(new XElement("ArcCenterMode", CbFormat.EnumText(m.ArcCenterMode)));
        if (m.MachiningOrigin.Point is { } o) el.Add(new XElement("MachiningOrigin", CbFormat.Pt2(o.XY)));
        foreach (var x in m.Extensions) el.Add(new XElement(x));
        return el;
    }

    private static XElement WritePart(Part part)
    {
        var el = new XElement("part", new XAttribute("Name", part.Name));
        el.Add(new XElement("machineops", part.Operations.Select(WriteOp)));
        el.Add(WriteStock(part.Stock));
        el.Add(new XElement("ToolProfile", CbFormat.EnumText(part.ToolProfile)));
        if (!part.Enabled) el.Add(new XElement("Enabled", "false"));
        if (part.MachiningOrigin.Point is { } o) el.Add(new XElement("MachiningOrigin", CbFormat.Pt2(o.XY)));
        if (!string.IsNullOrEmpty(part.OutFile)) el.Add(new XElement("OutFile", part.OutFile));
        if (!string.IsNullOrEmpty(part.Style)) el.Add(new XElement("Style", part.Style));
        if (!string.IsNullOrEmpty(part.StyleLibrary)) el.Add(new XElement("StyleLibrary", part.StyleLibrary));
        if (!string.IsNullOrEmpty(part.ToolLibrary)) el.Add(new XElement("ToolLibrary", part.ToolLibrary));
        if (part.ToolNumber != 0) el.Add(new XElement("ToolNumber", CbFormat.Int(part.ToolNumber)));
        if (part.ToolDiameter != 0) el.Add(new XElement("ToolDiameter", CbFormat.Double(part.ToolDiameter)));
        if (!string.IsNullOrEmpty(part.Tag)) el.Add(new XElement("Tag", part.Tag));

        var n = part.Nesting;
        var nest = new XElement("Nesting", new XElement("BasePoint", CbFormat.Pt2(n.BasePoint)));
        if (n.NestMethod != NestMethod.None) nest.Add(new XElement("NestMethod", CbFormat.EnumText(n.NestMethod)));
        if (n.Rows != 1) nest.Add(new XElement("Rows", CbFormat.Int(n.Rows)));
        if (n.Columns != 1) nest.Add(new XElement("Columns", CbFormat.Int(n.Columns)));
        if (n.Spacing != 0) nest.Add(new XElement("Spacing", CbFormat.Double(n.Spacing)));
        foreach (var x in n.Extensions) nest.Add(new XElement(x));
        el.Add(nest);
        if (part.NestingItems.Count > 0)
            el.Add(new XElement("NestingItem", part.NestingItems.Select(i => new XElement("NestingItem",
                new XElement("Location", CbFormat.Pt2(i.Location)),
                new XElement("Transform", new XAttribute("m", CbFormat.Matrix(i.Transform)))))));
        foreach (var x in part.Extensions) el.Add(new XElement(x));
        return el;
    }

    // Element orders observed in CamBam 1.0 files (type-specific first, then base properties).
    private const string BaseOrder =
        "StockSurface RoughingClearance ClearancePlane SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive Style Enabled " +
        "WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate " +
        "MaxCrossoverDistance StartPoint CustomMOPHeader CustomMOPFooter Name";

    private static readonly Dictionary<string, string[]> CanonicalOrder = new()
    {
        ["profile"] = ("TargetDepth DepthIncrement FinalDepthIncrement CutOrdering CutWidth StepOver StepoverFeedrate InsideOutside " +
                       "MillingDirection CollisionDetection CornerOvercut LeadInMove LeadOutMove HoldingTabs Tabs SideProfile " + BaseOrder).Split(' '),
        ["pocket"] = ("TargetDepth DepthIncrement FinalDepthIncrement CutOrdering StepOver StepoverFeedrate MillingDirection CollisionDetection " +
                      "LeadInMove LeadOutMove RegionFillStyle FinishStepover FinishStepoverAtTargetDepth " + BaseOrder).Split(' '),
        ["drill"] = ("TargetDepth DepthIncrement HoleDiameter DrillLeadOut SpiralFlatBase LeadOutLength DrillingMethod PeckDistance " +
                     "RetractHeight Dwell CustomScript " + BaseOrder).Split(' '),
        ["engrave"] = ("TargetDepth DepthIncrement FinalDepthIncrement " + BaseOrder).Split(' '),
        ["ncfile"] = ("SourceFile " + BaseOrder).Split(' '),
        // Only the older order (base properties first) has been observed for 3D profiles.
        ["surface3d"] = ("WorkPlane OptimisationMode RoughingFinishing ToolDiameter ToolNumber ToolProfile PlungeFeedrate CutFeedrate " +
                         "MaxCrossoverDistance StartPoint CustomMOPHeader CustomMOPFooter Name StockSurface RoughingClearance ClearancePlane " +
                         "SpindleDirection SpindleSpeed SpindleRange VelocityMode primitive Style Enabled DepthIncrement TargetDepth StepOver " +
                         "StepoverFeedrate Resolution ClipAreaMin ClipAreaMax StartCorner Profile3DMethod BackFaceCulling PlaneSliceOnly " +
                         "ScanlineGradientThreshold Mold Additive BackFace BackFaceZeroZ FlipAxis CutOrdering MillingDirection LeadInMove " +
                         "LeadOutMove BoundaryMethod BoundaryShapes BoundaryMargin BoundaryTaper RegionFillStyle").Split(' '),
    };

    private static XElement WriteOp(MachineOp op)
    {
        if (op is UnsupportedOp { RawXml: { } raw })
        {
            var copy = new XElement(raw);
            copy.Element("Name")?.SetValue(op.Name);
            var enabled = copy.Element("Enabled");
            if (!op.Enabled)
            {
                if (enabled == null) copy.Add(new XElement("Enabled", "false"));
                else enabled.Value = "false";
            }
            else enabled?.Remove();
            return copy;
        }

        var el = new XElement(op.XmlName);
        var parameters = op.Parameters().ToDictionary(p => p.Name, p => p.Param);
        var canonical = CanonicalOrder.TryGetValue(op.XmlName, out var c) ? c : BaseOrder.Split(' ');
        var order = op.ElementOrder.Count > 0 ? op.ElementOrder : canonical.ToList();
        var written = new HashSet<string>();
        var extQueue = op.Extensions.GroupBy(x => x.Name.LocalName).ToDictionary(g => g.Key, g => new Queue<XElement>(g));

        void WriteName(string name)
        {
            if (!written.Add(name) && name != "Tabs")
            {
                // Duplicate names only come from preserved unknown elements.
                if (extQueue.TryGetValue(name, out var dq) && dq.Count > 0) el.Add(new XElement(dq.Dequeue()));
                return;
            }
            switch (name)
            {
                case "Name": el.Add(new XElement("Name", op.Name)); return;
                case "Enabled": if (!op.Enabled) el.Add(new XElement("Enabled", "false")); return;
                case "Style": if (!string.IsNullOrEmpty(op.Style)) el.Add(new XElement("Style", op.Style)); return;
                case "Tag": if (!string.IsNullOrEmpty(op.Tag)) el.Add(new XElement("Tag", op.Tag)); return;
                case "primitive":
                    el.Add(new XElement("primitive", op.PrimitiveIds.Select(id => new XElement("prim", id))));
                    return;
                case "BoundaryShapes":
                    if (op is Profile3DOp p3 && (p3.BoundaryShapes.Count > 0 || op.ElementOrder.Contains("BoundaryShapes")))
                        el.Add(new XElement("BoundaryShapes", p3.BoundaryShapes.Select(id => new XElement("id", id))));
                    return;
                case "Tabs":
                    if (op is ProfileOp { Tabs.Count: > 0 } p)
                        el.Add(new XElement("Tabs", p.Tabs.Select(t => new XElement("HoldingTab",
                            t.NormalInverted ? new XElement("NormalInverted", "true") : null,
                            new XElement("ParentEntityID", t.ParentEntityID),
                            new XElement("ParametricPoint", CbFormat.Double(t.ParametricPoint)),
                            new XElement("Normal",
                                new XElement("X", CbFormat.Double(t.Normal.X)),
                                new XElement("Y", CbFormat.Double(t.Normal.Y)))))));
                    return;
            }
            if (parameters.TryGetValue(name, out var param))
            {
                el.Add(CbParams.Write(name, param));
                return;
            }
            if (extQueue.TryGetValue(name, out var q) && q.Count > 0) el.Add(new XElement(q.Dequeue()));
        }

        foreach (var name in order) WriteName(name);
        // Anything not covered by the stored order: canonical names, then explicitly set parameters, then extensions.
        foreach (var name in canonical)
            if (!written.Contains(name) && (name is "Name" or "primitive" || parameters.ContainsKey(name) ||
                                            name == "BoundaryShapes" && op is Profile3DOp { BoundaryShapes.Count: > 0 })) WriteName(name);
        foreach (var (name, param) in parameters)
            if (!written.Contains(name) && param.State != ParamState.Default) WriteName(name);
        if (!written.Contains("Enabled") && !op.Enabled) WriteName("Enabled");
        if (!written.Contains("Style") && !string.IsNullOrEmpty(op.Style)) WriteName("Style");
        if (!written.Contains("Tag") && !string.IsNullOrEmpty(op.Tag)) WriteName("Tag");
        if (!written.Contains("Tabs") && op is ProfileOp { Tabs.Count: > 0 }) WriteName("Tabs");
        foreach (var q in extQueue.Values)
            while (q.Count > 0) el.Add(new XElement(q.Dequeue()));
        return el;
    }
}
