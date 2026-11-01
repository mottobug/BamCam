using BamCam.App.Services;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Script objects use the global runner and approval hooks: run on their own.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public class ScriptTests
{
    [Fact]
    public void Default_script_draws_a_flange_with_holes()
    {
        var (paths, error) = ScriptRunner.Run(ScriptEntity.DefaultScript);
        Assert.Null(error);
        Assert.Equal(9, paths!.Count);
        Assert.Equal(80, paths.Select(p => p.Bounds()).Aggregate((a, b) => a.Union(b)).Width, 6);
    }

    [Fact]
    public void Examples_in_the_script_documentation_run()
    {
        // docs/scripts.md next to the repository root (tests run in tests/BamCam.Tests/bin/...).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "scripts.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "scripts.md")).Replace("\r\n", "\n");
        var blocks = System.Text.RegularExpressions.Regex.Matches(text, "```csharp\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(blocks.Count >= 4);
        foreach (System.Text.RegularExpressions.Match block in blocks)
        {
            var (paths, error) = ScriptRunner.Run(block.Groups[1].Value);
            Assert.True(error == null, $"{error}\n{block.Groups[1].Value}");
            Assert.NotEmpty(paths!);
        }
    }

    [Fact]
    public void Compile_and_run_errors_are_reported()
    {
        var (paths, error) = ScriptRunner.Run("Add(Circle(0, 0, ));");
        Assert.Null(paths);
        Assert.StartsWith(BamCam.Core.Localization.Tr.F("Line {0}: {1}", 1, ""), error);

        var (_, runError) = ScriptRunner.Run("throw new InvalidOperationException(\"boom\");");
        Assert.Equal("boom", runError);

        var (helpers, none) = ScriptRunner.Run("Add(Rect(0, 0, 10, 5)); Add(Arc(0, 0, 10, 0, 270)); Add(Polyline(true, 0, 0, 5, 0, 5, 5)); Add(Line(0, 0, 1, 1));");
        Assert.Null(none);
        Assert.Equal(4, helpers!.Count);
        Assert.Equal(270, Math.Round(helpers[1].Segments().Sum(s => s.IsArc ? Math.Abs(s.ToArc().Sweep) : 0) * 180 / Math.PI));
    }

    [Fact]
    public void Scripts_run_only_when_allowed_and_otherwise_show_the_saved_outlines()
    {
        var oldRunner = ScriptEntity.Runner;
        var oldAllowed = ScriptEntity.IsAllowed;
        try
        {
            ScriptEntity.Runner = ScriptRunner.Run;
            var allowed = new HashSet<string>();
            ScriptEntity.IsAllowed = allowed.Contains;

            var doc = new CadDocument();
            var script = doc.AddEntity(new ScriptEntity { Script = "Add(Rect(0, 0, 30, 20));" });
            Assert.False(script.IsComputed);                     // not allowed: nothing to show yet
            allowed.Add(ScriptEntity.Hash(script.Script));
            script.Invalidate();
            Assert.True(script.IsComputed);
            Assert.Equal(30, script.Bounds().Width, 9);

            // Saved with its outlines; read on a computer that has not allowed it: the saved outlines show.
            var xml = CbWriter.ToString(doc);
            allowed.Clear();
            var back = Assert.IsType<ScriptEntity>(Assert.Single(CbReader.Parse(xml).AllEntities()));
            Assert.False(back.IsComputed);
            Assert.Equal("Add(Rect(0, 0, 30, 20));", back.Script);
            Assert.Equal(30, back.Bounds().Width, 9);
            Assert.Equal(BamCam.Core.Localization.Tr.T("Not allowed to run: showing the saved outlines."), back.Status);
        }
        finally
        {
            ScriptEntity.Runner = oldRunner;
            ScriptEntity.IsAllowed = oldAllowed;
        }
    }
}
