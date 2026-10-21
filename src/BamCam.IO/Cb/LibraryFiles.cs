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
                    default: tool.Extensions.Add(new XElement(c)); break;
                }
            }
            lib.Tools.Add(tool);
        }
        return lib;
    }

    /// <summary>
    /// Resolves the style chain for an operation (MOP style → part style → machining style → root) and
    /// writes the resolved values into every parameter that is in the Default state.
    /// Returns the names of parameters whose cached value changed.
    /// </summary>
    public static List<string> ApplyStyle(CadDocument doc, MachineOp op)
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
