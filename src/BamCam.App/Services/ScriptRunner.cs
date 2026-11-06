using System.Collections.Concurrent;
using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace BamCam.App.Services;

/// <summary>One compile error of a script.</summary>
public sealed record ScriptProblem(
    [property: Browsable(false)] int Line,
    [property: Browsable(false)] int Column,
    [property: Browsable(false)] string Message);

/// <summary>Result of a script of the Scripts menu: stopped by the user, or an error text, or neither.</summary>
public sealed record ScriptRunResult([property: Browsable(false)] bool Stopped, [property: Browsable(false)] string? Error);

/// <summary>Compiles (cached) and runs C# scripts: those of script objects and those of the Scripts menu.</summary>
public static class ScriptRunner
{
    /// <summary>Longest a script object may run before BamCam stops waiting for it.</summary>
    public static TimeSpan TimeLimit { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly ScriptOptions ObjectOptions = ScriptOptions.Default
        .WithReferences(typeof(ScriptContext).Assembly, typeof(Enumerable).Assembly)
        .WithImports("System", "System.Linq", "System.Collections.Generic", "BamCam.Core.Geometry", "BamCam.Core.Model");

    private static readonly ScriptOptions MenuOptions = ObjectOptions
        .AddReferences(typeof(CommandScriptContext).Assembly)
        .AddImports("BamCam.Core.Cam");

    private static readonly ConcurrentDictionary<string, (Script<object>? Script, string? Error)> CompiledObjects = new();
    private static readonly ConcurrentDictionary<string, (Script<object>? Script, string? Error)> CompiledMenuScripts = new();

    private static Script<object> ObjectScript(string code) => CSharpScript.Create<object>(code, ObjectOptions, typeof(ScriptContext));
    private static Script<object> MenuScript(string code) => CSharpScript.Create<object>(code, MenuOptions, typeof(CommandScriptContext));

    /// <summary>All compile errors of a script object's script. Nothing of the script runs.</summary>
    public static IReadOnlyList<ScriptProblem> CheckObject(string code) => Problems(ObjectScript(code));

    /// <summary>All compile errors of a script of the Scripts menu. Nothing of the script runs.</summary>
    public static IReadOnlyList<ScriptProblem> CheckMenuScript(string code) => Problems(MenuScript(code));

    private static List<ScriptProblem> Problems(Script<object> script)
    {
        // Compile() stops at the syntax errors; the compilation also knows the wrong names beside them.
        var errors = script.GetCompilation().GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0) errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return errors.Select(d =>
        {
            var start = d.Location.GetLineSpan().StartLinePosition;
            return new ScriptProblem(start.Line + 1, start.Character + 1, d.GetMessage());
        }).OrderBy(p => p.Line).ThenBy(p => p.Column).ToList();
    }

    public static (IReadOnlyList<Path2>? Paths, string? Error) Run(string code)
    {
        var (script, error) = CompiledObjects.GetOrAdd(code, c => Compile(ObjectScript(c)));
        if (script == null) return (null, error);
        var context = new ScriptContext();
        try
        {
            var run = Task.Run(() => script.RunAsync(context));
            if (!run.Wait(TimeLimit)) return (null, BamCam.Core.Localization.Tr.F("The script runs longer than {0} s.", TimeLimit.TotalSeconds));
            return (context.Paths, null);
        }
        catch (AggregateException ex)
        {
            var inner = ex.InnerException is AggregateException agg ? agg.InnerException : ex.InnerException;
            return (null, inner?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Runs a script of the Scripts menu on a background thread, without a time limit. When <paramref name="cancel"/>
    /// fires this returns at once: the script can no longer touch the drawing, but .NET cannot end a loop that does not
    /// look at it, which then computes on until BamCam is closed.
    /// </summary>
    public static async Task<ScriptRunResult> RunMenuScript(string code, CommandScriptContext context, CancellationToken cancel)
    {
        var (script, error) = await Task.Run(() => CompiledMenuScripts.GetOrAdd(code, c => Compile(MenuScript(c))));
        if (script == null) return new ScriptRunResult(false, error);

        var run = Task.Run(() => script.RunAsync(context));
        var stopped = Task.Delay(Timeout.Infinite, cancel);
        if (await Task.WhenAny(run, stopped) == stopped)
        {
            _ = run.ContinueWith(t => t.Exception, TaskScheduler.Default); // a late failure is not reported any more
            return new ScriptRunResult(true, null);
        }
        try
        {
            await run;
            return new ScriptRunResult(false, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new ScriptRunResult(true, null);
        }
        catch (Exception ex)
        {
            return new ScriptRunResult(false, ex.Message);
        }
    }

    private static (Script<object>?, string?) Compile(Script<object> script)
    {
        var errors = Problems(script);
        if (errors.Count == 0) return (script, null);
        return (null, BamCam.Core.Localization.Tr.F("Line {0}: {1}", errors[0].Line, errors[0].Message) +
                      (errors.Count > 1 ? BamCam.Core.Localization.Tr.F(" (+{0} more)", errors.Count - 1) : ""));
    }
}
