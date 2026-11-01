using System.Text;

namespace BamCam.Core.Post;

/// <summary>
/// Post-build command of a post processor (as in CamBam): an external program run after the G-code file has been
/// written, e.g. to add line numbers. Its arguments understand {$outfile} (the G-code file) and {$cbfile.name}
/// (the drawing's name); arguments are split like a command line, double quotes keep spaces.
/// </summary>
public static class PostBuild
{
    /// <summary>Program and argument list, or null when the post processor has no post-build command.</summary>
    public static (string Program, List<string> Arguments)? Prepare(PostProcessorDefinition post, string outFile, string drawingName)
    {
        var program = post.PostBuildCommand.Trim().Trim('"');
        if (program.Length == 0) return null;
        var args = post.PostBuildCommandArgs
            .Replace("{$outfile}", outFile, StringComparison.OrdinalIgnoreCase)
            .Replace("{$cbfile.name}", drawingName, StringComparison.OrdinalIgnoreCase);
        return (program, SplitArguments(args));
    }

    /// <summary>
    /// Splits a command line into arguments: whitespace separates, double quotes group (and are removed), a doubled
    /// quote inside quotes is a literal quote.
    /// </summary>
    public static List<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArgument = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArgument) result.Add(current.ToString());
                current.Clear();
                hasArgument = false;
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }
        if (hasArgument) result.Add(current.ToString());
        return result;
    }

    /// <summary>Key under which the user's approval of a post-build command is remembered (program + raw arguments).</summary>
    public static string ApprovalKey(PostProcessorDefinition post) => $"{post.PostBuildCommand.Trim()}\n{post.PostBuildCommandArgs.Trim()}";
}
