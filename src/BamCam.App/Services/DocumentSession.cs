using BamCam.Core.Cam;
using BamCam.Core.Model;

namespace BamCam.App.Services;

/// <summary>
/// Holds the open document, the current selection, the undo history and generated toolpaths.
/// All UI parts talk to the document through this class.
/// </summary>
public sealed class DocumentSession
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly HashSet<Entity> _selection = [];

    public DocumentSession(CadDocument document) => Document = document;

    public CadDocument Document { get; private set; }

    /// <summary>Serialises/deserialises a document for undo snapshots (set by the IO layer).</summary>
    public Func<CadDocument, string>? Snapshot { get; init; }
    public Func<string, CadDocument>? Restore { get; init; }

    public Dictionary<MachineOp, Toolpath> Toolpaths { get; } = [];

    public IReadOnlyCollection<Entity> Selection => _selection;

    /// <summary>The object shown in the property grid (entity, layer, part, MOP or document).</summary>
    public object? Inspected { get; private set; }

    public event EventHandler? DocumentReplaced;
    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;
    public event EventHandler? InspectedChanged;
    public event EventHandler? ToolpathsChanged;
    public event EventHandler<string>? Message;

    public void Log(string message) => Message?.Invoke(this, message);

    public void Replace(CadDocument document)
    {
        Document = document;
        _undo.Clear();
        _redo.Clear();
        _selection.Clear();
        Toolpaths.Clear();
        Inspected = null;
        DocumentReplaced?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InspectedChanged?.Invoke(this, EventArgs.Empty);
        ToolpathsChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a modification as one undoable step.</summary>
    public void Execute(string description, Action action)
    {
        var before = Snapshot?.Invoke(Document);
        action();
        if (before != null)
        {
            _undo.Push(before);
            _redo.Clear();
        }
        Document.NotifyChanged();
        PruneSelection();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Notifies listeners after an in-place edit (e.g. property grid) that has been recorded via <see cref="BeginEdit"/>.</summary>
    public void NotifyChanged()
    {
        Document.NotifyChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the current state so that a following in-place edit can be undone.</summary>
    public void BeginEdit()
    {
        var before = Snapshot?.Invoke(Document);
        if (before == null) return;
        _undo.Push(before);
        _redo.Clear();
    }

    public bool CanUndo => _undo.Count > 0 && Restore != null;
    public bool CanRedo => _redo.Count > 0 && Restore != null;

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Snapshot!(Document));
        SwapTo(Restore!(_undo.Pop()));
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(Snapshot!(Document));
        SwapTo(Restore!(_redo.Pop()));
    }

    private void SwapTo(CadDocument restored)
    {
        restored.FilePath = Document.FilePath;
        restored.IsModified = true;
        var selectedIds = _selection.Select(e => e.Id).ToHashSet();
        Document = restored;
        _selection.Clear();
        foreach (var e in Document.AllEntities())
            if (selectedIds.Contains(e.Id)) _selection.Add(e);
        Toolpaths.Clear();
        Inspected = null;
        DocumentReplaced?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InspectedChanged?.Invoke(this, EventArgs.Empty);
        ToolpathsChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(IEnumerable<Entity> entities, bool add = false, bool toggle = false)
    {
        if (!add && !toggle) _selection.Clear();
        foreach (var e in entities)
        {
            if (toggle && !_selection.Add(e)) _selection.Remove(e);
            else _selection.Add(e);
        }
        OnSelectionChanged();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        OnSelectionChanged();
    }

    public void SelectAll() =>
        Select(Document.Layers.Where(l => l.Visible && !l.Locked).SelectMany(l => l.Entities));

    public bool IsSelected(Entity e) => _selection.Contains(e);

    private void OnSelectionChanged()
    {
        Inspected = _selection.Count == 1 ? _selection.First() : Inspected is Entity ? null : Inspected;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InspectedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Inspect(object? target)
    {
        Inspected = target;
        InspectedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PruneSelection()
    {
        var before = _selection.Count;
        _selection.RemoveWhere(e => e.Layer == null || !Document.Layers.Contains(e.Layer));
        if (before != _selection.Count) OnSelectionChanged();
    }

    public void SetToolpath(MachineOp op, Toolpath? path)
    {
        if (path == null) Toolpaths.Remove(op);
        else Toolpaths[op] = path;
        ToolpathsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearToolpaths()
    {
        Toolpaths.Clear();
        ToolpathsChanged?.Invoke(this, EventArgs.Empty);
    }
}
