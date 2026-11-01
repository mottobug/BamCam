using System.Diagnostics;

namespace BamCam.App.Services;

/// <summary>Result of an external program run: exit code (null = still running at the time limit) and its output.</summary>
public sealed record ProgramResult(int? ExitCode, string Output);

/// <summary>Runs an external program directly (no shell) and collects its output, with a time limit.</summary>
public static class ProgramRunner
{
    public static async Task<ProgramResult> RunAsync(string program, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The program could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            return new ProgramResult(null, "");
        }
        return new ProgramResult(process.ExitCode, (await output + "\n" + await errors).Trim());
    }
}