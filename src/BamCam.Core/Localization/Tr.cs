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
    private static Dictionary<string, string> _map = [];

    /// <summary>Folders searched for additional <c>{code}.json</c> files (later folders win).</summary>
    public static List<string> SearchFolders { get; } = [];

    /// <summary>Code of the active language ("en", "de", ...).</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Raised after the language was changed.</summary>
    public static event Action? Changed;

    /// <summary>Translates an English user interface text; returns it unchanged when there is no translation.</summary>
    public static string T(string english) =>
        _map.TryGetValue(english, out var s) && s.Length > 0 ? s : english;

    /// <summary>Translates a composite format string ("{0}: {1} points.") and formats it.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

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
        var map = code == "en" ? [] : Load(code);
        if (code != "en" && map.Count == 0) code = "en";
        var changed = code != Language || map.Count != _map.Count;
        _map = map;
        Language = code;
        if (changed) Changed?.Invoke();
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
                result[code] = map.TryGetValue(NameKey, out var n) && n.Length > 0 ? n : code;
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
        const string prefix = "BamCam.Core.Localization.lang.";
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".json")))
        {
            var resource = name;
            yield return (name[prefix.Length..^".json".Length], () => ReadResource(asm, resource));
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
