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

    private static PlasmaCutTable? TableOf(PlasmaCutData entry) => CamLibraries.PlasmaTables.FirstOrDefault(t => t.Entries.Contains(entry));

    /// <summary>A cut data entry was edited: save its table and give the linked plasma operations the new values.</summary>
    private void OnCutDataEdited(PlasmaCutData entry)
    {
        if (TableOf(entry) is not { } table) return;
        SaveCutTable(table);
        if (SystemTree.SelectedItem is Avalonia.Controls.TreeViewItem { Tag: PlasmaCutData } item) item.Header = entry.ToString();
        ApplyCutDataToOperations();
    }

    private void ApplyCutDataToOperations()
    {
        var doc = _session.Document;
        foreach (var op in doc.Parts.SelectMany(p => p.Operations).OfType<PlasmaOp>()) LibraryFiles.ApplyStyle(doc, op);
        _session.NotifyChanged();
    }

    private void SaveCutTable(PlasmaCutTable table)
    {
        try
        {
            LibraryFiles.SaveCutData(table, table.SourcePath ?? Path.Combine(SystemLibrary.PlasmaFolder, table.Name + ".xml"));
        }
        catch (Exception ex)
        {
            Log(F("Could not save cut data table '{0}': {1}", table.Name, ex.Message));
        }
    }

    /// <summary>
    /// [...] of a plasma operation's Cut Data: shows its entry in the System tab for editing, or the table in the
    /// drawing's units when the operation has none.
    /// </summary>
    private void ShowCutData(PlasmaOp op)
    {
        var doc = _session.Document;
        var units = doc.Units == Core.Model.DrawingUnits.Inches ? Core.Model.DrawingUnits.Inches : Core.Model.DrawingUnits.Millimeters;
        object? target = CamLibraries.FindCutData(doc, op.CutData.Value);
        if (target == null)
        {
            target = CamLibraries.PlasmaTables.FirstOrDefault(t => t.Units == units) ?? CamLibraries.PlasmaTables.FirstOrDefault();
            Log(T("Right-click a cut data table to add an entry, click an entry to edit it."));
        }
        LeftTabs.SelectedIndex = 1;
        if (target == null) return;

        static Avalonia.Controls.TreeViewItem? Find(IEnumerable<object?> items, object tag)
        {
            foreach (var item in items.OfType<Avalonia.Controls.TreeViewItem>())
            {
                if (ReferenceEquals(item.Tag, tag)) return item;
                if (Find(item.Items, tag) is { } found)
                {
                    item.IsExpanded = true;
                    return found;
                }
            }
            return null;
        }
        if (Find(SystemTree.Items, target) is not { } node) return;
        node.IsExpanded = true;
        SystemTree.SelectedItem = node;
        Avalonia.Controls.ControlExtensions.BringIntoView(node);
    }

    /// <summary>Adds a cut data entry, as a copy of <paramref name="template"/> when given.</summary>
    private void NewCutData(PlasmaCutTable table, PlasmaCutData? template)
    {
        var entry = template?.Clone() ?? new PlasmaCutData { Material = T("New Entry") };
        var baseName = template != null ? template.Name + " " + T("(copy)") : T("New Entry");
        entry.Name = baseName;
        for (var i = 2; table.Find(entry.Name) != null; i++) entry.Name = $"{baseName} {i}";
        table.Entries.Add(entry);
        SaveCutTable(table);
        BuildSystemTree();
        Properties.SelectedObject = entry;
    }

    private async void DeleteCutData(PlasmaCutTable table, PlasmaCutData entry)
    {
        if (await Dialogs.Ask(this, T("Delete Entry"), F("Delete '{0}' from '{1}'?", entry, table.Name), withCancel: false) != true) return;
        table.Entries.Remove(entry);
        SaveCutTable(table);
        BuildSystemTree();
        Properties.SelectedObject = _session.Inspected ?? _session.Document;
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
