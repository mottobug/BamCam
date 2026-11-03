using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.IO.Cb;

/// <summary>Reads CAM style libraries and tool libraries (CamBam-compatible XML) and applies styles to operations.</summary>
public static class LibraryFiles
{
    public static CamStyleLibrary LoadStyles(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException(Tr.T("Empty style library"));
        var lib = new CamStyleLibrary { Name = Path.GetFileNameWithoutExtension(path), SourcePath = path };
        foreach (var s in root.Elements("CAMStyle"))
        {
            var style = new CamStyle { Name = (string?)s.Attribute("Name") ?? "" };
            foreach (var el in s.Elements())
            {
                if (el.Name.LocalName == "Style") style.ParentStyle = el.Value;
                else style.Values[el.Name.LocalName] = new XElement(el);
            }
            lib.Styles.Add(style);
        }
        return lib;
    }

    /// <summary>Writes a CAM style library in the CamBam format (values kept as read or edited).</summary>
    public static void SaveStyles(CamStyleLibrary lib, string path)
    {
        var root = new XElement("CAMStyleLibrary", new XAttribute("Version", "0.9.8.0"));
        foreach (var style in lib.Styles)
        {
            var el = new XElement("CAMStyle", new XAttribute("Name", style.Name), new XElement("Style", style.ParentStyle));
            foreach (var value in style.Values.Values) el.Add(new XElement(value));
            root.Add(el);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
        lib.SourcePath = path;
    }

    /// <summary>
    /// Shows a style in the parameters of <paramref name="op"/> (a new operation of the type to edit): the values set
    /// in the style keep their state (Value / Auto), all others are Default with the value inherited from the parent
    /// styles.
    /// </summary>
    public static void LoadStyle(CamStyleLibrary lib, CamStyle style, MachineOp op)
    {
        var inherited = new Dictionary<string, XElement>();
        foreach (var s in lib.Chain(style.Name).Where(s => s != style))
        foreach (var (name, el) in s.Values)
            inherited[name] = el;
        foreach (var (name, param, _) in op.Parameters())
        {
            if (style.Values.TryGetValue(name, out var own))
            {
                CbParams.Read(param, own);
                continue;
            }
            if (inherited.TryGetValue(name, out var el)) CbParams.Read(param, el);
            param.State = ParamState.Default;
        }
    }

    /// <summary>Writes the parameters of <paramref name="op"/> back into the style: Default ones are removed (inherited).</summary>
    public static void StoreStyle(CamStyle style, MachineOp op)
    {
        foreach (var (name, param, _) in op.Parameters())
        {
            if (param.State == ParamState.Default) style.Values.Remove(name);
            else style.Values[name] = CbParams.Write(name, param);
        }
    }

    public static ToolLibrary LoadTools(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException(Tr.T("Empty tool library"));
        var lib = new ToolLibrary
        {
            Name = Path.GetFileNameWithoutExtension(path),
            SourcePath = path,
            ToolNameFormat = (string?)root.Attribute("ToolNameFormat") ?? "",
            NumberFormat = (string?)root.Attribute("NumberFormat") ?? "0.00",
        };
        foreach (var t in root.Elements("ToolDefinition"))
        {
            var tool = new ToolDefinition { Name = (string?)t.Attribute("Name") ?? "" };
            foreach (var c in t.Elements())
            {
                var v = c.Value;
                switch (c.Name.LocalName)
                {
                    case "Index": tool.Index = CbFormat.ParseInt(v); break;
                    case "Diameter": tool.Diameter = CbFormat.ParseDouble(v); break;
                    case "ToolProfile":
                        if (CbFormat.TryParseEnum(typeof(ToolProfile), v, out var p)) tool.ToolProfile = (ToolProfile)p;
                        break;
                    case "Flutes": tool.Flutes = CbFormat.ParseInt(v); break;
                    case "FluteLength": tool.FluteLength = CbFormat.ParseDouble(v); break;
                    case "Length": tool.Length = CbFormat.ParseDouble(v); break;
                    case "ShankDiameter": tool.ShankDiameter = CbFormat.ParseDouble(v); break;
                    case "HelixAngle": tool.HelixAngle = CbFormat.ParseDouble(v); break;
                    case "VeeAngle": tool.VeeAngle = CbFormat.ParseDouble(v); break;
                    case "MaxRampAngle": tool.MaxRampAngle = CbFormat.ParseDouble(v); break;
                    case "ToothLoad": tool.ToothLoad = CbFormat.ParseDouble(v); break;
                    case "AxialDepthOfCut": tool.AxialDepthOfCut = CbFormat.ParseDouble(v); break;
                    case "RadialDepthOfCut": tool.RadialDepthOfCut = CbFormat.ParseDouble(v); break;
                    case "Comment": tool.Comment = v; break;
                    case "LatheToolType":
                        if (CbFormat.TryParseEnum(typeof(LatheToolType), v, out var lt)) tool.LatheToolType = (LatheToolType)lt;
                        break;
                    case "TipOrientation": tool.TipOrientation = CbFormat.ParseInt(v); break;
                    default: tool.Extensions.Add(new XElement(c)); break;
                }
            }
            lib.Tools.Add(tool);
        }
        return lib;
    }

    /// <summary>
    /// Writes a tool library in the CamBam format. The lathe values (LatheToolType, TipOrientation) are BamCam
    /// additions and only written when set; unknown elements read from the file are written back.
    /// </summary>
    public static void SaveTools(ToolLibrary lib, string path)
    {
        string D(double v) => CbFormat.Double(v);
        var root = new XElement("ToolLibrary");
        if (lib.ToolNameFormat.Length > 0) root.Add(new XAttribute("ToolNameFormat", lib.ToolNameFormat));
        root.Add(new XAttribute("NumberFormat", lib.NumberFormat));
        foreach (var t in lib.Tools.OrderBy(t => t.Index))
        {
            var el = new XElement("ToolDefinition", new XAttribute("Name", t.Name),
                new XElement("Index", t.Index),
                new XElement("Diameter", D(t.Diameter)),
                new XElement("ToolProfile", t.ToolProfile),
                new XElement("Flutes", t.Flutes),
                new XElement("FluteLength", D(t.FluteLength)),
                new XElement("Length", D(t.Length)),
                new XElement("ShankDiameter", D(t.ShankDiameter)),
                new XElement("HelixAngle", D(t.HelixAngle)),
                new XElement("VeeAngle", D(t.VeeAngle)),
                new XElement("MaxRampAngle", D(t.MaxRampAngle)),
                new XElement("ToothLoad", D(t.ToothLoad)),
                new XElement("AxialDepthOfCut", D(t.AxialDepthOfCut)),
                new XElement("RadialDepthOfCut", D(t.RadialDepthOfCut)));
            if (t.Comment.Length > 0) el.Add(new XElement("Comment", t.Comment));
            if (t.LatheToolType != LatheToolType.None) el.Add(new XElement("LatheToolType", t.LatheToolType));
            if (t.TipOrientation != 0) el.Add(new XElement("TipOrientation", t.TipOrientation));
            foreach (var x in t.Extensions) el.Add(new XElement(x));
            root.Add(el);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
        lib.SourcePath = path;
    }

    /// <summary>Reads a plasma cut data table (BamCam format: one Entry element per row, values as child elements).</summary>
    public static PlasmaCutTable LoadCutData(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException(Tr.T("Empty cut data table"));
        var table = new PlasmaCutTable { Name = Path.GetFileNameWithoutExtension(path), SourcePath = path };
        if (CbFormat.TryParseEnum(typeof(DrawingUnits), (string?)root.Attribute("Units") ?? "", out var units)) table.Units = (DrawingUnits)units;
        foreach (var e in root.Elements("Entry"))
        {
            var entry = new PlasmaCutData { Name = (string?)e.Attribute("Name") ?? "" };
            foreach (var prop in CutDataProperties)
            {
                if (e.Element(prop.Name) is not { } el) continue;
                prop.SetValue(entry, prop.PropertyType == typeof(double) ? CbFormat.ParseDouble(el.Value) : el.Value);
            }
            table.Entries.Add(entry);
        }
        return table;
    }

    public static void SaveCutData(PlasmaCutTable table, string path)
    {
        var root = new XElement("PlasmaCutData", new XAttribute("Units", table.Units));
        foreach (var entry in table.Entries)
        {
            var el = new XElement("Entry", new XAttribute("Name", entry.Name));
            foreach (var prop in CutDataProperties)
            {
                var value = prop.GetValue(entry);
                if (value is string s && s.Length == 0) continue;
                el.Add(new XElement(prop.Name, value is double d ? CbFormat.Double(d) : value));
            }
            root.Add(el);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
        table.SourcePath = path;
    }

    private static readonly System.Reflection.PropertyInfo[] CutDataProperties = typeof(PlasmaCutData).GetProperties()
        .Where(p => p.CanWrite && p.Name != nameof(PlasmaCutData.Name) && (p.PropertyType == typeof(double) || p.PropertyType == typeof(string)))
        .ToArray();

    /// <summary>
    /// CAM styles the drawing refers to (machining options, parts, operations) that exist in no loaded style
    /// library. Operations using them keep the values stored in the drawing (see <see cref="ApplyStyle"/>).
    /// </summary>
    public static List<string> MissingStyles(CadDocument doc)
    {
        var names = new List<string?> { doc.Machining.Style };
        foreach (var part in doc.Parts)
        {
            names.Add(part.Style);
            names.AddRange(part.Operations.Select(o => o.Style));
        }
        return names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !CamLibraries.StyleLibraries.Any(l => l.Find(n) != null))
            .ToList();
    }

    /// <summary>
    /// Resolves the style chain for an operation (MOP style → part style → machining style → root) and
    /// writes the resolved values into every parameter that is in the Default state.
    /// Plasma operations then take the values of their cut data entry (which win over the style).
    /// Returns the names of parameters whose cached value changed.
    /// </summary>
    public static List<string> ApplyStyle(CadDocument doc, MachineOp op)
    {
        var changed = ApplyStyleValues(doc, op);
        if (op is PlasmaOp plasma && CamLibraries.FindCutData(doc, plasma.CutData.Value) is { } data)
            changed.AddRange(data.ApplyTo(plasma).Except(changed));
        return changed;
    }

    private static List<string> ApplyStyleValues(CadDocument doc, MachineOp op)
    {
        var changed = new List<string>();
        var part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
        var lib = CamLibraries.FindStyleLibrary(doc, part);
        if (lib == null) return changed;
        var styleName = !string.IsNullOrEmpty(op.Style) ? op.Style
            : !string.IsNullOrEmpty(part?.Style) ? part!.Style
            : doc.Machining.Style;
        if (!string.IsNullOrEmpty(styleName) && lib.Find(styleName) == null)
        {
            // The style may live in another library; if it is unknown on this computer (e.g. a drawing from
            // someone else's CamBam), keep the values stored in the drawing instead of silently using the
            // defaults of the root style (that changed feed rates and depths of edited operations).
            var other = CamLibraries.StyleLibraries.FirstOrDefault(l => l.Find(styleName) != null);
            if (other == null) return changed;
            lib = other;
        }

        var resolved = new Dictionary<string, XElement>();
        foreach (var style in lib.Chain(styleName))
        foreach (var (name, el) in style.Values)
            resolved[name] = el;

        // Default tool cascade: a tool chosen on the part or in the machining options wins over the style;
        // its diameter and profile come from the tool library.
        var cascadeTool = part is { ToolNumber: > 0 } ? part.ToolNumber : doc.Machining.ToolNumber;
        if (cascadeTool > 0)
        {
            var tool = CamLibraries.FindTool(doc, part, cascadeTool, op is TurningOp);
            void Set<T>(CamParam<T> p, T value, string name)
            {
                if (p.State != ParamState.Default || Equals(p.Value, value)) return;
                p.Value = value;
                p.RawXml = null;
                changed.Add(name);
            }
            Set(op.ToolNumber, cascadeTool, "ToolNumber");
            if (tool != null)
            {
                Set(op.ToolDiameter, tool.Diameter, "ToolDiameter");
                if (tool.ToolProfile != ToolProfile.Unspecified) Set(op.ToolProfile, tool.ToolProfile, "ToolProfile");
            }
            foreach (var key in new[] { "ToolNumber", "ToolDiameter", "ToolProfile" }) resolved.Remove(key);
        }

        foreach (var (name, param, _) in op.Parameters())
        {
            if (param.State != ParamState.Default || !resolved.TryGetValue(name, out var el)) continue;
            var before = param.BoxedValue;
            var temp = param.CloneParam();
            CbParams.Read(temp, el);
            if (temp.RawXml != null) continue;
            param.BoxedValue = temp.BoxedValue;
            // Auto values in styles (e.g. stock surface from stock) stay computed by the generator.
            if (temp.State == ParamState.Auto) param.State = ParamState.Auto;
            if (!Equals(before, param.BoxedValue))
            {
                changed.Add(name);
                param.RawXml = null; // a preserved original text would no longer match the value
            }
        }
        return changed;
    }
}
