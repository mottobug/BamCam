using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BamCam.Core.Localization;
using BamCam.Core.Post;

namespace BamCam.IO.Cb;

/// <summary>Reads and writes post processor definitions in the .cbpp XML format.</summary>
public static class CbppFile
{
    private static readonly Dictionary<string, PropertyInfo> Props = typeof(PostProcessorDefinition)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.Name != nameof(PostProcessorDefinition.Name))
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public static PostProcessorDefinition Load(string path)
    {
        var def = Parse(File.ReadAllText(path));
        def.Name = Path.GetFileNameWithoutExtension(path);
        return def;
    }

    public static PostProcessorDefinition Parse(string xml)
    {
        var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root
                   ?? throw new InvalidDataException(Tr.T("Empty post processor file"));
        var def = new PostProcessorDefinition();
        foreach (var el in root.Elements())
        {
            var name = el.Name.LocalName;
            var text = el.Value;
            if (!Props.TryGetValue(name, out var prop))
            {
                def.Unknown[name] = text;
                continue;
            }
            def.Explicit.Add(prop.Name);
            var t = prop.PropertyType;
            if (t == typeof(string)) prop.SetValue(def, text);
            else if (t == typeof(bool)) prop.SetValue(def, CbFormat.ParseBool(text));
            else if (t == typeof(double)) prop.SetValue(def, CbFormat.ParseDouble(text));
            else if (t == typeof(int)) prop.SetValue(def, CbFormat.ParseInt(text));
            else if (t.IsEnum && CbFormat.TryParseEnum(t, text, out var e)) prop.SetValue(def, e);
        }
        return def;
    }

    /// <summary>Writes the properties that differ from the built-in defaults (plus those present when loaded).</summary>
    public static void Save(PostProcessorDefinition def, string path)
    {
        var defaults = new PostProcessorDefinition();
        var root = new XElement("PostProcessor",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
            new XAttribute("Version", "0.9.8.0"));
        foreach (var prop in Props.Values.OrderBy(p => p.MetadataToken))
        {
            if (prop.PropertyType.IsGenericType) continue;
            var value = prop.GetValue(def);
            if (!def.Explicit.Contains(prop.Name) && Equals(value, prop.GetValue(defaults))) continue;
            var text = value switch
            {
                bool b => CbFormat.Bool(b, false),
                double d => CbFormat.Double(d),
                int i => CbFormat.Int(i),
                Enum en => CbFormat.EnumText(en),
                _ => value?.ToString() ?? "",
            };
            root.Add(new XElement(prop.Name, text));
        }
        foreach (var (k, v) in def.Unknown) root.Add(new XElement(k, v));

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\r\n", Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(path, settings);
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(w);
    }
}
