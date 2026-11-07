using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using BamCam.Plugins;

namespace BamCam.Tests;

/// <summary>Scripts of the Scripts menu and the syntax check of the script editor.</summary>
public class ScriptMenuTests
{
    private sealed class FakeCommands(CadDocument document, params double[] answers) : ICommandContext
    {
        private readonly Queue<double> _answers = new(answers);

        /// <summary>Answers to AskText; a null answer is a cancelled dialog. When empty, the default value.</summary>
        public Queue<string?> TextAnswers { get; } = new();

        public CadDocument Document { get; } = document;
        public List<Entity> Selected { get; } = [];
        public List<string> Messages { get; } = [];
        public int Changes { get; private set; }
        public IReadOnlyList<Entity> Selection => Selected;

        public void Change(string description, Action change)
        {
            Changes++;
            change();
        }

        public void AddEntities(string description, IEnumerable<Entity> entities) => Change(description, () =>
        {
            foreach (var e in entities) Document.AddEntity(e);
        });

        public void Select(IEnumerable<Entity> entities)
        {
            Selected.Clear();
            Selected.AddRange(entities);
        }

        public void Log(string message) => Messages.Add(message);
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Task.FromResult<double?>(_answers.Count > 0 ? _answers.Dequeue() : defaultValue);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : defaultValue);
        public void StartTool(PluginTool tool) { }
    }

    private static async Task<(ScriptRunResult Result, CommandScriptContext Context)> Run(string code, FakeCommands commands, CancellationToken cancel = default)
    {
        var context = new CommandScriptContext(commands, action => action(), cancel);
        return (await ScriptRunner.RunMenuScript(code, context, cancel), context);
    }

    private static string TempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamcam-scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------------------------------------------------------- check

    [Fact]
    public void Check_lists_all_errors_with_their_lines()
    {
        var problems = ScriptRunner.CheckObject("Add(Circle(0, 0, 1));\nAdd(Circel(0, 0, 1));\n\nvar x = 1 +;\n");

        Assert.Equal(2, problems.Count);
        Assert.Equal(2, problems[0].Line);
        Assert.Contains("Circel", problems[0].Message);
        Assert.Equal(4, problems[1].Line);
    }

    [Fact]
    public void Check_does_not_run_the_script()
    {
        var file = Path.Combine(TempFolder(), "ran.txt");
        var code = $"System.IO.File.WriteAllText(@\"{file}\", \"x\");";

        Assert.Empty(ScriptRunner.CheckMenuScript(code));
        Assert.Empty(ScriptRunner.CheckObject(code));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Menu_scripts_know_the_drawing_and_script_objects_do_not()
    {
        const string code = "Log(Document.Name);";
        Assert.Empty(ScriptRunner.CheckMenuScript(code));
        Assert.NotEmpty(ScriptRunner.CheckObject(code));
    }

    // ---------------------------------------------------------------- running

    [Fact]
    public async Task Script_sees_the_drawing_asks_and_adds_outlines()
    {
        var doc = new CadDocument();
        var commands = new FakeCommands(doc, 12);
        commands.Selected.Add(doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 0)], closed: false))));

        var (result, context) = await Run("""
            Log($"selected {Selection.Count}");
            var r = await AskNumber("t", "l", 1);
            Add(Circle(0, 0, r.Value));
            """, commands);

        Assert.Null(result.Error);
        Assert.False(result.Stopped);
        Assert.Equal(["selected 1"], commands.Messages);
        Assert.Equal(24, Assert.Single(context.Paths).Bounds().Width, 6);
    }

    [Fact]
    public async Task Change_changes_the_drawing()
    {
        var doc = new CadDocument();
        var commands = new FakeCommands(doc);

        var (result, _) = await Run("""
            Change("name it", () => Document.ActivePart.Name = "Renamed");
            AddEntities("add", [new PolylineEntity(Rect(0, 0, 5, 5))]);
            """, commands);

        Assert.Null(result.Error);
        Assert.Equal("Renamed", doc.ActivePart.Name);
        Assert.Single(doc.AllEntities());
        Assert.Equal(2, commands.Changes);
    }

    [Fact]
    public async Task Errors_are_reported_and_the_script_may_end_early()
    {
        var commands = new FakeCommands(new CadDocument());

        Assert.Equal("boom", (await Run("throw new InvalidOperationException(\"boom\");", commands)).Result.Error);
        Assert.NotNull((await Run("Nonsense();", commands)).Result.Error);
        var (ended, context) = await Run("""
            if (Selection.Count == 0) return;
            Add(Circle(0, 0, 1));
            """, commands);
        Assert.Null(ended.Error);
        Assert.Empty(context.Paths);
    }

    [Fact]
    public async Task Stop_returns_at_once_and_the_script_can_no_longer_change_the_drawing()
    {
        var doc = new CadDocument();
        var commands = new FakeCommands(doc);
        using var stop = new CancellationTokenSource();
        var folder = TempFolder();
        var started = Path.Combine(folder, "started");
        var left = Path.Combine(folder, "left");

        // A loop that never looks at Cancel; afterwards it tries to change the drawing.
        var run = Run($$"""
            System.IO.File.WriteAllText(@"{{started}}", "");
            while (!System.IO.File.Exists(@"{{left}}")) System.Threading.Thread.Sleep(5);
            Change("too late", () => Document.ActivePart.Name = "Changed");
            """, commands, stop.Token);
        for (var i = 0; i < 2000 && !File.Exists(started); i++) await Task.Delay(10);
        Assert.True(File.Exists(started));

        await stop.CancelAsync();
        var (result, _) = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Stopped);

        File.WriteAllText(left, "");
        await Task.Delay(200);
        Assert.Equal(0, commands.Changes);
    }

    [Fact]
    public async Task A_loop_that_checks_Cancel_ends_as_stopped()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var (result, _) = await Run("while (true) Cancel.ThrowIfCancellationRequested();", new FakeCommands(new CadDocument()), stop.Token);
        Assert.True(result.Stopped);
        Assert.Null(result.Error);
    }

    // ---------------------------------------------------------------- undo

    private static DocumentSession Session(CadDocument doc) => new(doc) { Snapshot = CbWriter.ToString, Restore = CbReader.Parse };

    [Fact]
    public void All_changes_of_a_run_are_one_undo_step()
    {
        var doc = CadDocument.CreateNew();
        var session = Session(doc);
        var group = new DocumentSession.UndoGroup();

        session.Execute(group, () => doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(1, 0)], closed: false))));
        session.Execute(group, () => doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(2, 0)], closed: false))));
        Assert.Equal(2, session.Document.AllEntities().Count());

        session.Undo();

        Assert.Empty(session.Document.AllEntities());
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void An_edit_of_the_user_in_between_starts_a_new_step()
    {
        var doc = CadDocument.CreateNew();
        var session = Session(doc);
        var group = new DocumentSession.UndoGroup();

        session.Execute(group, () => doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(1, 0)], closed: false))));
        session.Execute("user", () => doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(2, 0)], closed: false))));
        session.Execute(group, () => doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(3, 0)], closed: false))));

        session.Undo();

        Assert.Equal(2, session.Document.AllEntities().Count());
    }

    // ---------------------------------------------------------------- library

    [Fact]
    public void List_shows_folders_with_scripts_first()
    {
        var dir = TempFolder();
        Directory.CreateDirectory(Path.Combine(dir, "Sub", "Deeper"));
        Directory.CreateDirectory(Path.Combine(dir, "Empty"));
        File.WriteAllText(Path.Combine(dir, "b.csx"), "");
        File.WriteAllText(Path.Combine(dir, "A.csx"), "");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "");
        File.WriteAllText(Path.Combine(dir, "Sub", "Deeper", "c.csx"), "");

        var list = ScriptLibrary.List(dir);

        Assert.Equal(["Sub", "A", "b"], list.Select(e => e.Name));
        Assert.Equal("c", list[0].Children![0].Children![0].Name);
        Assert.Empty(ScriptLibrary.List(Path.Combine(dir, "missing")));
    }

    [Fact]
    public void Examples_are_written_once_and_deleted_ones_stay_deleted()
    {
        var dir = Path.Combine(TempFolder(), "scripts");
        ScriptLibrary.EnsureExamples(dir);
        var first = Assert.Single(ScriptLibrary.List(dir));
        Assert.Equal(ScriptLibrary.Examples.Count, first.Children!.Count);

        Directory.Delete(first.Path, true);
        ScriptLibrary.EnsureExamples(dir);

        Assert.Empty(ScriptLibrary.List(dir));
    }

    [Fact]
    public void Adding_a_file_copies_it_and_does_not_replace_without_being_told()
    {
        var from = TempFolder();
        var to = Path.Combine(TempFolder(), "scripts");
        var source = Path.Combine(from, "mine.csx");
        File.WriteAllText(source, "first");

        Assert.True(ScriptLibrary.Add(source, to, overwrite: false, out var target));
        Assert.Equal("first", File.ReadAllText(target));

        File.WriteAllText(source, "second");
        Assert.False(ScriptLibrary.Add(source, to, overwrite: false, out _));
        Assert.Equal("first", File.ReadAllText(target));
        Assert.True(ScriptLibrary.Add(source, to, overwrite: true, out _));
        Assert.Equal("second", File.ReadAllText(target));
        Assert.True(ScriptLibrary.Add(target, to, overwrite: false, out _)); // already there
    }

    [Fact]
    public void Scripts_are_in_the_configuration_backup_and_their_approvals_are_not()
    {
        var system = TempFolder();
        var settingsFile = Path.Combine(system, "settings.json");
        Directory.CreateDirectory(Path.Combine(system, "scripts"));
        File.WriteAllText(Path.Combine(system, "scripts", "mine.csx"), "Add(Circle(0, 0, 1));");
        var settings = new AppSettings();
        settings.ApprovedScripts.Add("hash");
        settings.Save(settingsFile);
        var zip = Path.Combine(TempFolder(), "backup.zip");
        ConfigTransfer.Export(system, settingsFile, zip);

        var other = TempFolder();
        var otherSettings = Path.Combine(other, "settings.json");
        ConfigTransfer.Import(zip, other, otherSettings);

        Assert.True(File.Exists(Path.Combine(other, "scripts", "mine.csx")));
        Assert.Empty(AppSettings.Load(otherSettings, out _).ApprovedScripts);
    }

    public static TheoryData<string> ExampleNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in ScriptLibrary.Examples) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public async Task Examples_run(string name)
    {
        var text = ScriptLibrary.Examples.Single(e => e.Name == name).Text;
        Assert.Empty(ScriptRunner.CheckMenuScript(text));
        var doc = CadDocument.CreateNew();
        var commands = new FakeCommands(doc);
        commands.Selected.Add(doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 5)], closed: false))));

        var (result, _) = await Run(text, commands);

        Assert.Null(result.Error);
    }

    // ---------------------------------------------------------------- Squircle as a script and as a plugin

    private static string SquircleScript => ScriptLibrary.Examples.Single(e => e.Name == "Squircle").Text;

    /// <summary>Runs the script: the texts and numbers are the answers to its dialogs, in order.</summary>
    private static async Task<(IReadOnlyList<Path2> Paths, FakeCommands Commands)> RunSquircle(string?[] texts, params double[] numbers)
    {
        var commands = new FakeCommands(CadDocument.CreateNew(), numbers);
        foreach (var text in texts) commands.TextAnswers.Enqueue(text);
        var (result, context) = await Run(SquircleScript, commands);
        Assert.Null(result.Error);
        return ([.. context.Paths], commands);
    }

    private static void AssertSameOutline(Squircle.SquircleEntity plugin, IReadOnlyList<Path2> script)
    {
        var expected = Assert.Single(plugin.ToPaths());
        var actual = Assert.Single(script);
        Assert.True(actual.Closed);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(expected.Vertices[i].XY.AlmostEquals(actual.Vertices[i].XY, 1e-9), $"point {i}: {expected.Vertices[i].XY} and {actual.Vertices[i].XY}");
    }

    [Fact]
    public async Task Squircle_script_with_its_defaults_draws_what_the_plugin_inserts()
    {
        var (paths, _) = await RunSquircle([]);

        AssertSameOutline(new Squircle.SquircleEntity(), paths);
    }

    [Theory]
    [InlineData(2, 30, 20, 5, -3, 0.5)]
    [InlineData(1.5, 10, 10, 0, 0, 2)]
    [InlineData(0.5, 40, 80, 1, 1, 1)]
    public async Task Squircle_script_makes_the_simple_shapes_of_the_plugin(double n1, double rx, double ry, double x, double y, double angle)
    {
        var (paths, _) = await RunSquircle(["simple", I($"{rx} {ry} {x} {y}"), "Angle"], n1, angle);

        AssertSameOutline(new Squircle.SquircleEntity { N1 = n1, RadX = rx, RadY = ry, OrigX = x, OrigY = y, Angle = angle }, paths);
    }

    [Theory]
    [InlineData(4, 4, 4, 2, 1, 1.2)]
    [InlineData(3, 1, 1, 1, 1, 1)]
    [InlineData(5, 2, 7, 7, 1, 1)]
    [InlineData(8, 0.5, 1, 3, 2, 0.5)]
    public async Task Squircle_script_makes_the_complex_shapes_of_the_plugin(double m, double n1, double n2, double n3, double a, double b)
    {
        var (paths, _) = await RunSquircle(["Complex", I($"{m} {n1} {n2} {n3} {a} {b}"), "60 40 2 3", "EdgeLength"], 1.5);

        AssertSameOutline(new Squircle.SquircleEntity
        {
            ShapeType = Squircle.SquircleShape.Complex, M = m, N1 = n1, N2 = n2, N3 = n3, A = a, B = b,
            RadX = 60, RadY = 40, OrigX = 2, OrigY = 3, Accuracy = Squircle.SquircleAccuracy.EdgeLength, EdgeLength = 1.5,
        }, paths);
    }

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    [Fact]
    public async Task Squircle_script_limits_the_points_like_the_plugin_and_says_so()
    {
        var (paths, commands) = await RunSquircle(["Simple", "50 50 0 0", "Angle"], 4, 0.001);

        AssertSameOutline(new Squircle.SquircleEntity { Angle = 0.001 }, paths);
        Assert.Equal(Squircle.SquircleEntity.MostPoints, paths[0].Count);
        Assert.Contains(commands.Messages, m => m.Contains("limited to 20000"));
    }

    [Theory]
    [InlineData(new[] { "Round" }, "unknown shape type")]
    [InlineData(new[] { "Simple", "50 50 0" }, "expected numbers")]
    [InlineData(new[] { "Simple", "50 x 0 0" }, "expected numbers")]
    [InlineData(new[] { "Simple", "50 50 0 0", "Fine" }, "unknown accuracy")]
    public async Task Squircle_script_says_what_is_wrong_and_draws_nothing(string[] texts, string message)
    {
        var (paths, commands) = await RunSquircle(texts);

        Assert.Empty(paths);
        Assert.Contains(commands.Messages, m => m.Contains(message));
    }

    [Fact]
    public async Task Squircle_script_stops_when_a_dialog_is_cancelled()
    {
        var (paths, commands) = await RunSquircle(["Simple", null]);

        Assert.Empty(paths);
        Assert.Empty(commands.Messages);
    }

    [Fact]
    public async Task Squircle_script_takes_a_comma_as_decimal_point()
    {
        var (paths, _) = await RunSquircle(["Simple", "12,5 50 0 0"]);

        AssertSameOutline(new Squircle.SquircleEntity { RadX = 12.5 }, paths);
    }

    [Fact]
    public void Menu_script_examples_in_the_documentation_compile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "scripts.md"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "scripts.md")).Replace("\r\n", "\n");
        var blocks = System.Text.RegularExpressions.Regex.Matches(text, "```cs\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(blocks.Count >= 2);
        foreach (System.Text.RegularExpressions.Match block in blocks)
            Assert.Empty(ScriptRunner.CheckMenuScript(block.Groups[1].Value));
    }

    private static string ReferenceText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "script-reference.md"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs", "script-reference.md")).Replace("\r\n", "\n");
    }

    [Fact]
    public async Task Examples_of_the_script_reference_run()
    {
        var blocks = System.Text.RegularExpressions.Regex.Matches(ReferenceText(), "```cs\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(blocks.Count >= 7);
        foreach (System.Text.RegularExpressions.Match block in blocks)
        {
            var code = block.Groups[1].Value;
            var doc = CadDocument.CreateNew();
            var circle = doc.AddEntity(new CircleEntity { Center = new Point3(5, 5, 0), Diameter = 10 });
            var profile = new ProfileOp { Name = "profile" };
            profile.PrimitiveIds.Add(circle.Id);
            doc.ActivePart.Operations.Add(profile);
            var commands = new FakeCommands(doc);
            commands.Selected.Add(circle);

            var (result, _) = await Run(code, commands);

            Assert.True(result.Error == null, $"{result.Error}\n{code}");
        }
    }

    [Fact]
    public async Task Examples_of_the_script_reference_do_what_they_say()
    {
        var blocks = System.Text.RegularExpressions.Regex.Matches(ReferenceText(), "```cs\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        var doc = CadDocument.CreateNew();
        var circle = doc.AddEntity(new CircleEntity { Center = new Point3(5, 5, 0), Diameter = 10 });
        var commands = new FakeCommands(doc);
        commands.Selected.Add(circle);

        await Run(blocks.Single(b => b.Contains("Drill circles")), commands);
        var drill = Assert.IsType<DrillOp>(Assert.Single(doc.ActivePart.Operations));
        Assert.Equal([circle.Id], drill.PrimitiveIds);
        Assert.Equal(-5, drill.TargetDepth.Value);

        await Run(blocks.Single(b => b.Contains("Holes")), commands);
        Assert.Equal(5, doc.Layers.Single(l => l.Name.StartsWith("Holes", StringComparison.Ordinal)).EntityCount);

        var before = doc.AllEntities().Count();
        await Run(blocks.Single(b => b.Contains("CadOps.Offset")), commands);
        Assert.Equal(before + 1, doc.AllEntities().Count());

        // Moved by 10 in X, then rotated by 45 degrees around the origin: a * b applies a first.
        await Run(blocks.Single(b => b.Contains("Move selection")), commands);
        var expected = new Point2(15, 5).Rotate(Math.PI / 4, Point2.Zero);
        Assert.True(circle.Bounds().Center.XY.AlmostEquals(expected, 1e-6), circle.Bounds().Center.ToString());
    }

    [Fact]
    public void Everything_named_in_the_script_reference_exists()
    {
        var assemblies = new[] { typeof(CadDocument).Assembly, typeof(CommandScriptContext).Assembly };
        var types = assemblies.SelectMany(a => a.GetExportedTypes()).ToLookup(t => t.Name);
        var missing = new List<string>();
        var checkedTypes = 0;
        Type? current = null;
        foreach (var line in ReferenceText().Split('\n'))
        {
            var heading = System.Text.RegularExpressions.Regex.Match(line, @"^### `(\w+)`");
            if (heading.Success)
            {
                var name = heading.Groups[1].Value;
                current = types[name].FirstOrDefault() ?? types[name + "`1"].FirstOrDefault();
                if (current == null) missing.Add(name);
                else checkedTypes++;
                continue;
            }
            if (line.StartsWith("#", StringComparison.Ordinal)) current = null;
            if (current == null || !line.StartsWith("| `", StringComparison.Ordinal)) continue;
            var cell = line.Split('|')[1];
            foreach (System.Text.RegularExpressions.Match member in System.Text.RegularExpressions.Regex.Matches(cell, @"`(\w+)"))
                if (current.GetMember(member.Groups[1].Value, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy).Length == 0)
                    missing.Add($"{current.Name}.{member.Groups[1].Value}");
        }
        Assert.True(checkedTypes > 30, $"{checkedTypes} types");
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Fact]
    public void New_script_text_compiles()
    {
        Assert.Empty(ScriptRunner.CheckMenuScript(ScriptLibrary.NewScript));
    }
}
