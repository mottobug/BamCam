using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.IO.Cb;

/// <summary>Reads and writes state-wrapped machining parameters (<c>&lt;X state="Default"&gt;value&lt;/X&gt;</c>).</summary>
internal static class CbParams
{
    public static void Read(ICamParam param, XElement el)
    {
        param.State = (string?)el.Attribute("state") switch
        {
            "Value" => ParamState.Value,
            "Auto" => ParamState.Auto,
            _ => ParamState.Default,
        };
        var type = param.ValueType;
        var empty = !el.HasElements && string.IsNullOrEmpty(el.Value);
        try
        {
            if (type == typeof(LeadMove))
            {
                param.BoxedValue = empty ? LeadMove.None : new LeadMove(
                    ParseEnum<LeadMoveType>(el.Element("LeadInType")?.Value),
                    CbFormat.ParseDouble(el.Element("SpiralAngle")?.Value),
                    CbFormat.ParseDouble(el.Element("TangentRadius")?.Value),
                    CbFormat.ParseDouble(el.Element("LeadInFeedrate")?.Value));
                KeepUnknownChildren(param, el, "LeadInType", "SpiralAngle", "TangentRadius", "LeadInFeedrate");
            }
            else if (type == typeof(HoldingTabSettings))
            {
                if (!empty)
                {
                    var d = new HoldingTabSettings();
                    param.BoxedValue = new HoldingTabSettings(
                        ParseEnum<TabMethod>(el.Element("TabMethod")?.Value),
                        CbFormat.ParseDouble(el.Element("Width")?.Value, d.Width),
                        CbFormat.ParseDouble(el.Element("Height")?.Value, d.Height),
                        CbFormat.ParseInt(el.Element("MinimumTabs")?.Value, d.MinimumTabs),
                        CbFormat.ParseInt(el.Element("MaximumTabs")?.Value, d.MaximumTabs),
                        CbFormat.ParseDouble(el.Element("TabDistance")?.Value, d.TabDistance),
                        CbFormat.ParseDouble(el.Element("SizeThreshold")?.Value, d.SizeThreshold),
                        CbFormat.ParseBool(el.Element("UseLeadIns")?.Value),
                        el.Element("TabStyle") is { } ts ? ParseEnum<TabStyle>(ts.Value) : d.TabStyle);
                }
                KeepUnknownChildren(param, el, "TabMethod", "Width", "Height", "MinimumTabs", "MaximumTabs",
                    "TabDistance", "SizeThreshold", "UseLeadIns", "TabStyle");
            }
            else if (type == typeof(SideProfile))
            {
                if (!empty)
                    param.BoxedValue = new SideProfile(
                        ParseEnum<SideProfileMethod>(el.Element("Method")?.Value),
                        CbFormat.ParseDouble(el.Element("Value")?.Value),
                        CbFormat.ParseBool(el.Element("AdjustCutWidth")?.Value));
                KeepUnknownChildren(param, el, "Method", "Value", "AdjustCutWidth");
            }
            else if (type == typeof(Matrix4))
            {
                param.BoxedValue = CbFormat.ParseMatrix((string?)el.Attribute("m") ?? el.Value);
            }
            else if (el.HasElements)
            {
                // Unexpected structure for a scalar: keep verbatim.
                param.RawXml = new XElement(el);
            }
            else if (empty)
            {
                if (type == typeof(string)) param.BoxedValue = "";
                else if (type == typeof(OptionalPoint)) param.BoxedValue = OptionalPoint.None;
            }
            else if (!TryParseScalar(type, el.Value, out var value))
            {
                // Values from a localised CamBam ("Eintauch-Vorschub"): understood, but written back verbatim
                // (RawXml) so the file stays as that CamBam wrote it until the value is edited.
                if (type.IsEnum && CbFormat.TryParseLocalizedEnum(type, el.Value, out var localized))
                    param.BoxedValue = localized;
                param.RawXml = new XElement(el);
            }
            else
            {
                param.BoxedValue = value;
            }
        }
        catch (FormatException)
        {
            param.RawXml = new XElement(el);
        }
    }

    private static void KeepUnknownChildren(ICamParam param, XElement el, params string[] known)
    {
        if (el.Elements().Any(c => !known.Contains(c.Name.LocalName))) param.RawXml = new XElement(el);
    }

    private static T ParseEnum<T>(string? text) where T : struct, Enum =>
        CbFormat.TryParseEnum(typeof(T), text, out var v) ? (T)v : default;

    public static bool TryParseScalar(Type type, string text, out object? value)
    {
        value = null;
        if (type == typeof(string)) { value = text; return true; }
        if (type == typeof(double))
        {
            if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return false;
            value = d;
            return true;
        }
        if (type == typeof(int))
        {
            value = CbFormat.ParseInt(text);
            return true;
        }
        if (type == typeof(bool))
        {
            var t = text.Trim().ToLowerInvariant();
            if (t is not ("true" or "false")) return false;
            value = t == "true";
            return true;
        }
        if (type.IsEnum)
        {
            if (!CbFormat.TryParseEnum(type, text, out var e)) return false;
            value = e;
            return true;
        }
        if (type == typeof(OptionalPoint))
        {
            value = string.IsNullOrWhiteSpace(text) ? OptionalPoint.None : new OptionalPoint(CbFormat.ParsePoint3(text));
            return true;
        }
        if (type == typeof(Matrix4))
        {
            value = CbFormat.ParseMatrix(text);
            return true;
        }
        if (type == typeof(Point2))
        {
            value = CbFormat.ParsePoint2(text);
            return true;
        }
        return false;
    }

    public static XElement Write(string name, ICamParam param)
    {
        var state = param.State switch
        {
            ParamState.Value => "Value",
            ParamState.Auto => "Auto",
            _ => "Default",
        };
        if (param.RawXml != null)
        {
            var raw = new XElement(param.RawXml) { Name = name };
            raw.SetAttributeValue("state", state);
            return raw;
        }

        var el = new XElement(name, new XAttribute("state", state));
        switch (param.BoxedValue)
        {
            case LeadMove lm:
                if (!lm.IsNone || param.State != ParamState.Default)
                {
                    el.Add(new XElement("LeadInType", CbFormat.EnumText(lm.LeadMoveType)),
                        new XElement("SpiralAngle", CbFormat.Double(lm.SpiralAngle)),
                        new XElement("TangentRadius", CbFormat.Double(lm.TangentRadius)),
                        new XElement("LeadInFeedrate", CbFormat.Double(lm.LeadMoveFeedrate)));
                }
                break;
            case HoldingTabSettings t:
                el.Add(new XElement("TabMethod", CbFormat.EnumText(t.TabMethod)),
                    new XElement("Width", CbFormat.Double(t.Width)),
                    new XElement("Height", CbFormat.Double(t.Height)),
                    new XElement("MinimumTabs", CbFormat.Int(t.MinimumTabs)),
                    new XElement("MaximumTabs", CbFormat.Int(t.MaximumTabs)),
                    new XElement("TabDistance", CbFormat.Double(t.TabDistance)),
                    new XElement("SizeThreshold", CbFormat.Double(t.SizeThreshold)),
                    new XElement("UseLeadIns", CbFormat.Bool(t.UseLeadIns, true)),
                    new XElement("TabStyle", CbFormat.EnumText(t.TabStyle)));
                break;
            case SideProfile sp:
                if (sp.Method != SideProfileMethod.None)
                {
                    el.Add(new XElement("Method", CbFormat.EnumText(sp.Method)),
                        new XElement("Value", CbFormat.Double(sp.Value)),
                        new XElement("AdjustCutWidth", CbFormat.Bool(sp.AdjustCutWidth, true)));
                }
                break;
            case double d: el.Value = CbFormat.Double(d); break;
            case int i: el.Value = CbFormat.Int(i); break;
            case bool b: el.Value = CbFormat.Bool(b, true); break;
            case string s: if (s.Length > 0) el.Value = s; break;
            case OptionalPoint op: if (op.Point is { } p) el.Value = CbFormat.Pt3(p); break;
            case Matrix4 m: el.Add(new XAttribute("m", CbFormat.Matrix(m))); break;
            case Point2 p2: el.Value = CbFormat.Pt2(p2); break;
            case Enum e: el.Value = CbFormat.EnumText(e); break;
            case { } other: el.Value = other.ToString() ?? ""; break;
        }
        return el;
    }
}
