using System.ComponentModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BamCam.App.Controls;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using Xunit.Abstractions;

namespace BamCam.Tests;

/// <summary>
/// Checks the built-in translations against every user interface text: string literals passed to the
/// translation functions and menu helpers, attribute texts, and property / enum names shown in the property grid.
/// A failing completeness test writes the missing entries to %TEMP%/bamcam-missing-{code}.json.
/// The tests never switch the active language (it is global and other tests run in parallel).
/// </summary>
public partial class LocalizationTests(ITestOutputHelper output)
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    // Literal arguments: T("..."), F("...", …), Tr.T(...), menu helpers, toolbar buttons, attributes and type names.
    [GeneratedRegex("""(?:ToolButton\("\w+",|\[Param\(\d+,|\b(?:T|F|Tr\.T|Tr\.F|Item|Check|Sub|Category|DisplayName|Description)\s*\()\s*"((?:[^"\\]|\\.)*)"(?!\s*\+)""")]
    private static partial Regex LiteralCall();

    [GeneratedRegex("""\b(?:TypeName|DisplayType)\s*=>\s*"((?:[^"\\]|\\.)*)";""")]
    private static partial Regex TypeNames();

    /// <summary>All English texts the user interface can show.</summary>
    public static SortedSet<string> CollectKeys()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal) { "Misc" };
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith("Tr.cs")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in LiteralCall().Matches(text)) Add(keys, Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in TypeNames().Matches(text)) Add(keys, Regex.Unescape(m.Groups[1].Value));
        }

        // Property grid: names and categories of displayed properties, humanized enum members.
        var assemblies = new[] { typeof(MachineOp).Assembly, typeof(PropertyGrid).Assembly };
        var enums = new HashSet<Type> { typeof(ParamState) };
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsPublic && t.IsClass))
        {
            if (type.Namespace is not ("BamCam.Core.Model" or "BamCam.Core.Cam" or "BamCam.Core.Post" or "BamCam.App.Services")) continue;
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (prop.GetIndexParameters().Length > 0 || prop.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
                var valueType = prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(CamParam<>)
                    ? prop.PropertyType.GetGenericArguments()[0]
                    : prop.PropertyType;
                var shown = PropertyGrid.IsEditableType(valueType) || PropertyGrid.IsCompositeType(valueType) ||
                            valueType == typeof(List<Vertex>) || valueType == typeof(List<Point3>);
                if (!shown) continue;
                keys.Add(PropertyGrid.DisplayNameOf(prop));
                keys.Add(PropertyGrid.CategoryOf(prop));
                if (valueType.IsEnum) enums.Add(valueType);
            }
        }
        foreach (var e in enums)
            foreach (var name in Enum.GetNames(e))
                keys.Add(e == typeof(ParamState) ? name : PropertyGrid.Humanize(name));
        return keys;
    }

    private static void Add(SortedSet<string> keys, string s)
    {
        // Pure symbols, numbers or placeholders need no translation.
        if (s.Any(char.IsLetter) && !Regex.IsMatch(s, @"^\{\d+[^}]*\}$")) keys.Add(s);
    }

    [Fact]
    public void Collects_a_plausible_number_of_texts()
    {
        var keys = CollectKeys();
        output.WriteLine($"{keys.Count} texts");
        Assert.True(keys.Count > 400, $"only {keys.Count} texts found");
        Assert.Contains("_Generate Toolpaths", keys);
        Assert.Contains("Step Over", keys);
        Assert.Contains("Depth First", keys);
    }

    [Theory]
    [InlineData("de")]
    public void Translation_is_complete(string code)
    {
        var map = Tr.Load(code);
        Assert.NotEmpty(map);
        var missing = CollectKeys().Where(k => !map.TryGetValue(k, out var v) || v.Length == 0).ToList();
        if (missing.Count > 0)
        {
            var path = Path.Combine(Path.GetTempPath(), $"bamcam-missing-{code}.json");
            var json = JsonSerializer.Serialize(missing.ToDictionary(k => k, _ => ""),
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(path, json);
            output.WriteLine($"Missing entries written to {path}");
        }
        Assert.True(missing.Count == 0, $"{missing.Count} texts without '{code}' translation, e.g.: " + string.Join(" | ", missing.Take(15)));
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [Theory]
    [InlineData("de")]
    public void Translations_keep_placeholders_and_access_keys(string code)
    {
        var problems = new List<string>();
        foreach (var (key, value) in Tr.Load(code))
        {
            if (value.Length == 0 || key == "_language") continue;
            var a = Placeholder().Matches(key).Select(m => m.Value).OrderBy(x => x);
            var b = Placeholder().Matches(value).Select(m => m.Value).OrderBy(x => x);
            if (!a.SequenceEqual(b)) problems.Add($"placeholders: {key} → {value}");
            if (a.Any()) try
            {
                _ = string.Format(value, Enumerable.Range(0, 10).Select(i => (object)i).ToArray());
            }
            catch (FormatException)
            {
                problems.Add($"format: {value}");
            }
            // Menu access keys: a single underscore marker like the English text (doubled "__" is a literal underscore).
            var keyHas = Regex.IsMatch(key, "(?<!_)_(?!_)");
            var valueMarks = Regex.Matches(value, "(?<!_)_(?!_)").Count;
            if (keyHas && valueMarks != 1 || !keyHas && valueMarks != 0) problems.Add($"access key: {key} → {value}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Unused_entries_are_reported()
    {
        var keys = CollectKeys();
        var unused = Tr.Load("de").Keys.Where(k => k != "_language" && !keys.Contains(k)).ToList();
        // Not a failure: texts may be composed at run time. Listed to keep the file tidy.
        foreach (var k in unused) output.WriteLine("unused: " + k);
    }

    [Fact]
    public void German_is_available_and_english_is_the_fallback()
    {
        var languages = Tr.Available();
        Assert.Contains(languages, l => l.Code == "en");
        Assert.Contains(languages, l => l.Code == "de" && l.Name == "Deutsch");
        Assert.Equal("Some text without translation", Tr.T("Some text without translation"));
    }
}
