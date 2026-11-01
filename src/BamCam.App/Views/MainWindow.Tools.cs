using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>Libraries in BamCam's own tool folder can be edited; those of a CamBam installation are read-only.</summary>
    private static bool IsOwnLibrary(ToolLibrary lib) =>
        lib.SourcePath != null && Path.GetFullPath(lib.SourcePath).StartsWith(
            Path.GetFullPath(Path.Combine(SystemLibrary.SystemFolder, "tools")), StringComparison.OrdinalIgnoreCase);

    private static ToolLibrary? LibraryOf(ToolDefinition tool) => CamLibraries.ToolLibraries.FirstOrDefault(l => l.Tools.Contains(tool));

    /// <summary>A tool was edited in the properties: save its library (own libraries only).</summary>
    private void OnToolEdited(ToolDefinition tool)
    {
        if (LibraryOf(tool) is not { } lib) return;
        if (!IsOwnLibrary(lib))
        {
            Log(F("Tool library '{0}' belongs to CamBam and is not changed on disk. Copy it to {1} to edit it.", lib.Name,
                Path.Combine(SystemLibrary.SystemFolder, "tools")));
            return;
        }
        SaveToolLibrary(lib);
        if (SystemTree.SelectedItem is Avalonia.Controls.TreeViewItem { Tag: ToolDefinition } item) item.Header = tool.ToString();
        _session.NotifyChanged(); // tool diameters of the operations may have changed
    }

    private void SaveToolLibrary(ToolLibrary lib)
    {
        try
        {
            LibraryFiles.SaveTools(lib, lib.SourcePath!);
        }
        catch (Exception ex)
        {
            Log(F("Could not save tool library '{0}': {1}", lib.Name, ex.Message));
        }
    }

    /// <summary>Adds a tool with the next free number, as a copy of <paramref name="template"/> when given.</summary>
    private void NewTool(ToolLibrary lib, ToolDefinition? template)
    {
        var tool = template?.Clone() ?? new ToolDefinition { Name = T("New Tool") };
        tool.Index = lib.Tools.Count == 0 ? 1 : lib.Tools.Max(t => t.Index) + 1;
        lib.Tools.Add(tool);
        SaveToolLibrary(lib);
        BuildSystemTree();
        Properties.SelectedObject = tool;
    }

    private async void DeleteTool(ToolLibrary lib, ToolDefinition tool)
    {
        if (await Dialogs.Ask(this, T("Delete Tool"), F("Delete tool {0} from '{1}'?", tool, lib.Name), withCancel: false) != true) return;
        lib.Tools.Remove(tool);
        SaveToolLibrary(lib);
        BuildSystemTree();
        Properties.SelectedObject = _session.Inspected ?? _session.Document;
    }
}
