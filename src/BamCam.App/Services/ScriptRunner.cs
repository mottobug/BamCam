using System.Collections.Concurrent;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace BamCam.App.Services;

/// <summary>Compiles (cached) and runs the C# scripts of script objects.</summary>
public static class ScriptRunner
{
    /// <summary>Longest a script may run before BamCam stops waiting for it.</summary>
    public static TimeSpan TimeLimit { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(typeof(ScriptContext).Assembly, typeof(Enumerable).Assembly)
        .WithImports("System", "System.Linq", "System.Collections.Generic", "BamCam.Core.Geometry", "BamCam.Core.Model");

    private static readonly ConcurrentDictionary<string, (Script<object>? Script, string? Error)> Compiled = new();

    public static (IReadOnlyList<Path2>? Paths, string? Error) Run(string code)
    {
        var (script, error) = Compiled.GetOrAdd(code, Compile);
        if (script == null) return (null, error);
        var context = new ScriptContext();
        try
        {
            var run = Task.Run(() => script.RunAsync(context));
            if (!run.Wait(TimeLimit)) return (null, $"The script runs longer than {TimeLimit.TotalSeconds:0} s.");
            return (context.Paths, null);
        }
        catch (AggregateException ex)
        {
            var inner = ex.InnerException is AggregateException agg ? agg.InnerException : ex.InnerException;
            return (null, inner?.Message ?? ex.Message);
        }
    }

    private static (Script<object>?, string?) Compile(string code)
    {
        var script = CSharpScript.Create<object>(code, Options, typeof(ScriptContext));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0) return (script, null);
        var first = errors[0];
        var line = first.Location.GetLineSpan().StartLinePosition.Line + 1;
        return (null, $"Line {line}: {first.GetMessage()}" + (errors.Count > 1 ? $" (+{errors.Count - 1} more)" : ""));
    }
}
