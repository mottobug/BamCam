using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Cb;

/// <summary>Lexical conventions of CamBam-compatible XML files (numbers, points, colours, enums).</summary>
internal static class CbFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- numbers -----------------------------------------------------------------------------

    public static double ParseDouble(string? s, double fallback = 0) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, Inv, out var v) ? v : fallback;

    public static int ParseInt(string? s, int fallback = 0) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, Inv, out var v) ? v
        : double.TryParse(s?.Trim(), NumberStyles.Float, Inv, out var d) ? (int)Math.Round(d) : fallback;

    public static bool ParseBool(string? s, bool fallback = false) =>
        s?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => fallback,
        };

    /// <summary>Scalar doubles: shortest round-trip representation.</summary>
    public static string Double(double v) => v.ToString("R", Inv);

    /// <summary>Components inside composite strings (points, matrices): 15 significant digits.</summary>
    public static string Component(double v)
    {
        if (Math.Abs(v) < 1e-300) v = 0;
        return v.ToString("G15", Inv);
    }

    public static string Int(int v) => v.ToString(Inv);

    /// <summary>"True"/"False" for state-wrapped values, "true"/"false" elsewhere.</summary>
    public static string Bool(bool v, bool capitalised) => capitalised ? (v ? "True" : "False") : (v ? "true" : "false");

    // ---- points ------------------------------------------------------------------------------

    public static Point3 ParsePoint3(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Point3.Zero;
        var parts = s.Split(',');
        return new Point3(
            parts.Length > 0 ? ParseDouble(parts[0]) : 0,
            parts.Length > 1 ? ParseDouble(parts[1]) : 0,
            parts.Length > 2 ? ParseDouble(parts[2]) : 0);
    }

    public static Point2 ParsePoint2(string? s) => ParsePoint3(s).XY;

    public static string Pt3(Point3 p) => $"{Component(p.X)},{Component(p.Y)},{Component(p.Z)}";

    /// <summary>"x,y" when Z is exactly zero, otherwise "x,y,z".</summary>
    public static string Pt3OmitZ(Point3 p) =>
        p.Z == 0 ? $"{Component(p.X)},{Component(p.Y)}" : Pt3(p);

    public static string Pt2(Point2 p) => $"{Component(p.X)},{Component(p.Y)}";

    // ---- colours -----------------------------------------------------------------------------

    public static RgbColor ParseColor(string? s, RgbColor fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var parts = s.Split(',');
        if (parts.Length < 3) return fallback;
        byte B(string x) => (byte)Math.Clamp(ParseInt(x), 0, 255);
        return new RgbColor(B(parts[0]), B(parts[1]), B(parts[2]));
    }

    public static string Color(RgbColor c) => $"{c.R},{c.G},{c.B}";

    // ---- matrices ----------------------------------------------------------------------------

    public static Matrix4 ParseMatrix(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Trim().Equals("Identity", StringComparison.OrdinalIgnoreCase))
            return Matrix4.Identity;
        var parts = s.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 16) return Matrix4.Identity;
        return Matrix4.FromArray(parts.Select(p => ParseDouble(p)).ToArray());
    }

    public static string Matrix(Matrix4 m) =>
        m.IsIdentity ? "Identity" : string.Join(' ', m.ToArray().Select(Component));

    // ---- enums -------------------------------------------------------------------------------

    public static bool TryParseEnum(Type enumType, string? text, out object value)
    {
        value = Activator.CreateInstance(enumType)!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        var compact = t.Replace(" ", "").Replace("(", "").Replace(")", "").Replace("-", "");
        foreach (var f in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var member = f.GetCustomAttribute<EnumMemberAttribute>()?.Value;
            if (string.Equals(member, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Name, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Name.Replace("_", ""), compact, StringComparison.OrdinalIgnoreCase))
            {
                value = f.GetValue(null)!;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Localised UI texts → English, for enum values written by a localised CamBam (e.g. "Eintauch-Vorschub").
    /// Seeded with common German values; more come from translation files via <see cref="CbLocalization"/>.
    /// </summary>
    internal static readonly Dictionary<string, string> LocalizedToEnglish = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Eintauch-Vorschub"] = "Plunge Feedrate",
        ["Vorschub"] = "Cut Feedrate",
    };

    /// <summary>Parses an enum value given in a translated form.</summary>
    public static bool TryParseLocalizedEnum(Type enumType, string? text, out object value)
    {
        value = Activator.CreateInstance(enumType)!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (LocalizedToEnglish)
        {
            return LocalizedToEnglish.TryGetValue(text.Trim(), out var english) && TryParseEnum(enumType, english, out value);
        }
    }

    public static string EnumText(object value)
    {
        var type = value.GetType();
        var name = Enum.GetName(type, value)!;
        var member = type.GetField(name)?.GetCustomAttribute<EnumMemberAttribute>()?.Value;
        return member ?? name;
    }
}
