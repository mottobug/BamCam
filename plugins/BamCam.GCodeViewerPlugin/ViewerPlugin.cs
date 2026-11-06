using System.Diagnostics;
using BamCam.Plugins;

namespace BamCam.GCodeViewerPlugin;

/// <summary>A G-code viewer in the browser.</summary>
public sealed record GCodeViewer(string Name, string Url, string AccessKeyName);

/// <summary>
/// Machining ▸ Open in ...: writes the G-code of the drawing to a temporary file, opens the viewer in the browser and
/// shows the file in the file manager, ready to be dragged into the browser window. These viewers offer no documented way
/// to hand over a file, so this last step stays with the user. The G-code goes to no machine: no post-build command runs.
/// </summary>
public sealed class ViewerPlugin : IBamCamPlugin
{
    /// <summary>The viewers offered. The first one also gets a toolbar button.</summary>
    public static readonly IReadOnlyList<GCodeViewer> Viewers =
    [
        new("CutViewer", "https://cutviewer.com/app/", "_CutViewer"),
        new("NC Viewer", "https://ncviewer.com/", "_NC Viewer"),
    ];

    private const string EyeIcon = "M2,12 C6,5 18,5 22,12 C18,19 6,19 2,12 Z M12,9 A3,3 0 1 0 12,15 A3,3 0 1 0 12,9 Z";

    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        foreach (var viewer in Viewers)
            host.AddCommand(new PluginCommand
            {
                Header = german ? $"In {viewer.AccessKeyName} öffnen" : $"Open in {viewer.AccessKeyName}",
                Menu = "Machining",
                Description = german
                    ? $"G-Code in {viewer.Name} im Browser ansehen; die Datei wird im Explorer gezeigt und ins Browserfenster gezogen. Geht nicht an die Maschine."
                    : $"View the G-code in {viewer.Name} in the browser; the file is shown in the file manager, to be dragged into the browser window. Nothing goes to the machine.",
                Icon = viewer == Viewers[0] ? EyeIcon : null,
                Execute = ctx => Open(ctx, viewer, german),
            });
    }

    /// <summary>Folder of the temporary G-code files.</summary>
    public static string TempFolder => Path.Combine(Path.GetTempPath(), "BamCam-GCodeViewer");

    /// <param name="start">Starts a program; tests replace it.</param>
    /// <param name="folder">Where the file goes; by default <see cref="TempFolder"/>.</param>
    public static async Task Open(ICommandContext context, GCodeViewer viewer, bool german, Action<ProcessStartInfo>? start = null, string? folder = null)
    {
        var text = await context.PostGCode();
        if (text == null) return;
        start ??= info => Process.Start(info)?.Dispose();
        try
        {
            var path = WriteFile(text, context.Document.Name, folder ?? TempFolder);
            start(RevealCommand(path));
            start(new ProcessStartInfo(viewer.Url) { UseShellExecute = true });
            context.Log(german
                ? $"{viewer.Name}: Datei ins Browserfenster ziehen oder dort öffnen: {path}"
                : $"{viewer.Name}: drag the file into the browser window or open it there: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            context.Log(german ? $"{viewer.Name} konnte nicht geöffnet werden: {ex.Message}" : $"Could not open {viewer.Name}: {ex.Message}");
        }
    }

    /// <summary>Writes the G-code named after the drawing; the file of an earlier run is replaced.</summary>
    public static string WriteFile(string text, string drawingName, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = string.Concat(drawingName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var path = Path.Combine(folder, (name.Length == 0 ? "drawing" : name) + ".nc");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Shows the file in the file manager.</summary>
    public static ProcessStartInfo RevealCommand(string path)
    {
        if (OperatingSystem.IsWindows()) return new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"");
        if (OperatingSystem.IsMacOS()) return new ProcessStartInfo("open", ["-R", path]);
        return new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(path)!]);
    }
}
