using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>Libraries in BamCam's own tool folder can be edited; those of a CamBam installation are read-only.</summary>
    private static bool IsOwnLibrary(ToolLibrary lib) => IsOwnLibrary(lib.SourcePath, "tools");

    /// <summary>A library file in BamCam's own folder <paramref name="sub"/> (styles, tools) of the system folder.</summary>
    private static bool IsOwnLibrary(string? path, string sub) =>
        path != null && Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(Path.Combine(SystemLibrary.SystemFolder, sub)), StringComparison.OrdinalIgnoreCase);

    private string UnitsSuffix => _session.Document.Units == Core.Model.DrawingUnits.Inches ? "in" : "mm";

    /// <summary>
    /// Copies a library of a CamBam installation into BamCam's own folder under the same name; the copy is loaded
    /// instead of the original and can be edited.
    /// </summary>
    private void CopyLibrary(string? source, string sub)
    {
        if (source == null || !File.Exists(source)) return;
        var target = Path.Combine(SystemLibrary.SystemFolder, sub, Path.GetFileName(source));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(source, target);
        }
        catch (Exception ex)
        {
            Log(F("Could not copy '{0}': {1}", source, ex.Message));
            return;
        }
        ReloadLibraries();
        Log(F("Copied to {0}; this copy is used from now on and can be edited.", target));
    }

    private void ReloadLibraries()
    {
        _system.Reload();
        BuildSystemTree();
        foreach (var op in _session.Document.Parts.SelectMany(p => p.Operations)) LibraryFiles.ApplyStyle(_session.Document, op);
        _session.NotifyChanged();
    }

    private async void NewStyleLibrary()
    {
        var name = (await Dialogs.Prompt(this, T("New Style Library"), T("Name of the library:"), $"My-{UnitsSuffix}"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (CamLibraries.StyleLibraries.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Log(F("A style library '{0}' exists already.", name));
            return;
        }
        var lib = new CamStyleLibrary { Name = name };
        lib.Styles.Add(new CamStyle { Name = "" });
        try { LibraryFiles.SaveStyles(lib, Path.Combine(SystemLibrary.SystemFolder, "styles", name + ".xml")); }
        catch (Exception ex)
        {
            Log(F("Could not save style library '{0}': {1}", name, ex.Message));
            return;
        }
        CamLibraries.StyleLibraries.Add(lib);
        BuildSystemTree();
        Log(F("Style library '{0}' created. Choose it in the properties of Machining (Style Library); a library named Standard-{1} is used by default.", name, UnitsSuffix));
    }

    /// <summary>A new style in the library, as a copy of <paramref name="template"/> when given (else inheriting the default).</summary>
    private async void NewStyle(CamStyleLibrary lib, CamStyle? template)
    {
        var suggestion = template != null ? template.Name + " " + T("(copy)") : T("New Style");
        var name = (await Dialogs.Prompt(this, T("New Style"), T("Name of the style:"), suggestion.Trim()))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (lib.Find(name) != null)
        {
            Log(F("A style '{0}' exists already.", name));
            return;
        }
        var style = new CamStyle { Name = name, ParentStyle = template?.ParentStyle ?? "" };
        if (template != null)
            foreach (var (key, value) in template.Values) style.Values[key] = new System.Xml.Linq.XElement(value);
        lib.Styles.Add(style);
        SaveStyleLibrary(lib);
        BuildSystemTree();
        EditStyle(lib, style);
    }

    private async void DeleteStyle(CamStyleLibrary lib, CamStyle style)
    {
        if (await Dialogs.Ask(this, T("Delete Style"), F("Delete style '{0}' from '{1}'?", style, lib.Name), withCancel: false) != true) return;
        // Styles inheriting from it inherit from its parent instead.
        foreach (var child in lib.Styles.Where(s => s.ParentStyle == style.Name)) child.ParentStyle = style.ParentStyle;
        lib.Styles.Remove(style);
        SaveStyleLibrary(lib);
        BuildSystemTree();
        foreach (var op in _session.Document.Parts.SelectMany(p => p.Operations)) LibraryFiles.ApplyStyle(_session.Document, op);
        _session.NotifyChanged();
    }

    /// <summary>The open style windows: one per style, opening it again brings it to the front.</summary>
    private readonly Dictionary<CamStyle, StyleEditorWindow> _styleWindows = [];

    private void EditStyle(CamStyleLibrary lib, CamStyle style)
    {
        if (_styleWindows.TryGetValue(style, out var open))
        {
            open.Activate();
            return;
        }
        var own = IsOwnLibrary(lib.SourcePath, "styles");
        var window = new StyleEditorWindow(lib, style, !own, () =>
        {
            // Operations with Default values follow the edited style.
            foreach (var op in _session.Document.Parts.SelectMany(p => p.Operations)) LibraryFiles.ApplyStyle(_session.Document, op);
            _session.NotifyChanged();
        });
        _styleWindows[style] = window;
        window.Closed += (_, _) => _styleWindows.Remove(style);
        window.Show(this);
    }

    private void SaveStyleLibrary(CamStyleLibrary lib)
    {
        try { LibraryFiles.SaveStyles(lib, lib.SourcePath!); }
        catch (Exception ex) { Log(F("Could not save style library '{0}': {1}", lib.Name, ex.Message)); }
    }

    private async void NewToolLibrary()
    {
        var name = (await Dialogs.Prompt(this, T("New Tool Library"), T("Name of the library:"), $"My-{UnitsSuffix}"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (CamLibraries.ToolLibraries.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Log(F("A tool library '{0}' exists already.", name));
            return;
        }
        var lib = new ToolLibrary { Name = name };
        try { LibraryFiles.SaveTools(lib, Path.Combine(SystemLibrary.SystemFolder, "tools", name + ".xml")); }
        catch (Exception ex)
        {
            Log(F("Could not save tool library '{0}': {1}", name, ex.Message));
            return;
        }
        CamLibraries.ToolLibraries.Add(lib);
        BuildSystemTree();
        NewTool(lib, null);
        Log(F("Tool library '{0}' created. Choose it in the properties of Machining (Tool Library); a library named Default-{1} is used by default.", name, UnitsSuffix));
    }

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
