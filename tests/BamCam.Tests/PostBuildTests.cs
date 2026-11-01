using BamCam.App.Services;
using BamCam.Core.Post;

namespace BamCam.Tests;

public class PostBuildTests
{
    [Fact]
    public void Macros_are_replaced_and_quoted_arguments_kept_together()
    {
        var post = new PostProcessorDefinition
        {
            PostBuildCommand = "\"C:\\bin\\gcode linenums.exe\"",
            PostBuildCommandArgs = "\"{$outfile}\" \"{$OUTFILE}.out\" -n {$cbfile.name}",
        };
        var (program, args) = PostBuild.Prepare(post, @"C:\My Files\part.nc", "Bracket")!.Value;
        Assert.Equal(@"C:\bin\gcode linenums.exe", program);
        Assert.Equal([@"C:\My Files\part.nc", @"C:\My Files\part.nc.out", "-n", "Bracket"], args);
    }

    [Fact]
    public void No_command_no_run()
    {
        Assert.Null(PostBuild.Prepare(new PostProcessorDefinition { PostBuildCommandArgs = "x" }, "a.nc", "a"));
    }

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("\"say \"\"hi\"\"\" end", new[] { "say \"hi\"", "end" })]
    [InlineData("pre\"fix with\"space", new[] { "prefix withspace" })]
    public void Command_line_splitting(string line, string[] expected)
    {
        Assert.Equal(expected, PostBuild.SplitArguments(line));
    }

    [Fact]
    public void Approval_key_changes_with_the_command_or_its_arguments()
    {
        var a = new PostProcessorDefinition { PostBuildCommand = "tool", PostBuildCommandArgs = "\"{$outfile}\"" };
        var b = new PostProcessorDefinition { PostBuildCommand = "tool", PostBuildCommandArgs = "\"{$outfile}\" --delete" };
        Assert.NotEqual(PostBuild.ApprovalKey(a), PostBuild.ApprovalKey(b));
    }

    [Fact]
    public async Task Program_runs_without_a_shell_and_reports_exit_code_and_output()
    {
        // dotnet is available wherever the tests run.
        var ok = await ProgramRunner.RunAsync("dotnet", ["--version"], Path.GetTempPath(), TimeSpan.FromSeconds(60));
        Assert.Equal(0, ok.ExitCode);
        Assert.Matches(@"^\d+\.\d+", ok.Output);
        var failed = await ProgramRunner.RunAsync("dotnet", ["no-such-command-xyz"], Path.GetTempPath(), TimeSpan.FromSeconds(60));
        Assert.NotEqual(0, failed.ExitCode);
    }
}