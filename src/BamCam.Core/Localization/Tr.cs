using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace BamCam.Core.Localization;

/// <summary>A language that can be selected: code ("de"), native name ("Deutsch").</summary>
public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// User interface translation. English is the source language: the English text is the key and is shown
/// whenever no translation exists. Other languages are JSON files <c>{code}.json</c> mapping English text to
/// the translation; the special key <c>"_language"</c> holds the language's own name ("Deutsch").
/// Built-in languages are embedded in this assembly; files in the <see cref="SearchFolders"/> add new
/// languages or override single entries without rebuilding the application.
/// </summary>
public static class Tr
{
    private const string NameKey = "_language";
    private const string ResourcePrefix = "BamCam.Core.Localization.lang.";
    private static Dictionary<string, string> _map = [];

    /// <summary>Folders searched for additional <c>{code}.json</c> files (later folders win).</summary>
    public static List<string> SearchFolders { get; } = [];

    /// <summary>Code of the active language ("en", "de", ...).</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>
    /// Number and date format of the active language: the regional settings of the operating system when they
    /// belong to the same language (de-AT, en-GB, ...), otherwise the language's neutral culture.
    /// </summary>
    public static CultureInfo Culture { get; private set; } = CultureFor("en");

    /// <summary>Raised after the language was changed.</summary>
    public static event Action? Changed;

    /// <summary>Translates an English user interface text; returns it unchanged when there is no translation.</summary>
    public static string T(string english) =>
        _map.TryGetValue(english, out var s) && s.Length > 0 ? s : english;

    /// <summary>Translates a composite format string ("{0}: {1} points.") and formats it.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(Culture, T(englishFormat), args);

    /// <summary>True when the active language has a translation for the text.</summary>
    public static bool Has(string english) => _map.TryGetValue(english, out var s) && s.Length > 0;

    /// <summary>
    /// Selects a language by code. An empty code selects the operating system language when it is available,
    /// otherwise English. Unknown codes fall back to English.
    /// </summary>
    public static void SetLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) code = SystemLanguage();
        code = code.Trim().ToLowerInvariant();
        // English needs no file, but an external en.json may still rename single texts.
        var map = Load(code);
        if (code != "en" && map.Count == 0) code = "en";
        var changed = code != Language || map.Count != _map.Count;
        _map = map;
        Language = code;
        Culture = CultureFor(code);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Reads the files of the active language again (after the user edited one) and raises <see cref="Changed"/>.</summary>
    public static void Reload()
    {
        _map = Load(Language);
        Changed?.Invoke();
    }

    /// <summary>The entries built into the application for a language (without external files).</summary>
    public static Dictionary<string, string> BuiltIn(string code)
    {
        var asm = typeof(Tr).Assembly;
        var name = $"{ResourcePrefix}{code}.json";
        return asm.GetManifestResourceNames().Contains(name) ? ReadResource(asm, name) : [];
    }

    /// <summary>
    /// Text of a new user language file: the header as comment lines, then every built-in entry as a commented
    /// line. Removing the <c>//</c> of a line overrides that text; everything else keeps following the built-in
    /// translation (also after updates).
    /// </summary>
    public static string Template(string code, IEnumerable<string> header)
    {
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sb = new System.Text.StringBuilder();
        foreach (var line in header) sb.Append("// ").AppendLine(line);
        sb.AppendLine("{");
        var builtIn = BuiltIn(code);
        if (!builtIn.ContainsKey(NameKey) && code != "en") sb.AppendLine($"  \"{NameKey}\": {JsonSerializer.Serialize(code, options)},");
        foreach (var (key, value) in builtIn)
            sb.AppendLine($"  // {JsonSerializer.Serialize(key, options)}: {JsonSerializer.Serialize(value, options)},");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Number format for a language code (see <see cref="Culture"/>).</summary>
    public static CultureInfo CultureFor(string code)
    {
        var system = CultureInfo.CurrentCulture;
        if (system.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)) return system;
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Operating system language if a translation exists for it, else "en".</summary>
    public static string SystemLanguage()
    {
        var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Available().Any(l => l.Code == code) ? code : "en";
    }

    /// <summary>All selectable languages: English plus every built-in or external translation.</summary>
    public static IReadOnlyList<LanguageInfo> Available()
    {
        var result = new Dictionary<string, string> { ["en"] = "English" };
        foreach (var (code, read) in Sources())
        {
            try
            {
                var map = read();
                // A file that only overrides single entries keeps the name given before (built-in or "English").
                if (map.TryGetValue(NameKey, out var n) && n.Length > 0) result[code] = n;
                else result.TryAdd(code, code);
            }
            catch (Exception)
            {
                // Broken file: not offered.
            }
        }
        return result.Select(kv => new LanguageInfo(kv.Key, kv.Value)).OrderBy(l => l.Code == "en" ? "" : l.Name).ToList();
    }

    /// <summary>The entries of one language (built-in, then external files overriding single entries).</summary>
    public static Dictionary<string, string> Load(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (c, read) in Sources())
        {
            if (c != code) continue;
            try
            {
                foreach (var kv in read()) map[kv.Key] = kv.Value;
            }
            catch (Exception)
            {
                // A broken external file must not prevent the application from starting.
            }
        }
        return map;
    }

    /// <summary>Parses a language file: a flat JSON object of strings (comments and trailing commas allowed).</summary>
    public static Dictionary<string, string> Parse(Stream json)
    {
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, options) ?? [];
    }

    private static IEnumerable<(string Code, Func<Dictionary<string, string>> Read)> Sources()
    {
        var asm = typeof(Tr).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix) && n.EndsWith(".json")))
        {
            var resource = name;
            yield return (name[ResourcePrefix.Length..^".json".Length], () => ReadResource(asm, resource));
        }
        foreach (var folder in SearchFolders.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                var path = file;
                yield return (Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), () =>
                {
                    using var s = File.OpenRead(path);
                    return Parse(s);
                });
            }
        }
    }

    private static Dictionary<string, string> ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)!;
        return Parse(s);
    }
}
