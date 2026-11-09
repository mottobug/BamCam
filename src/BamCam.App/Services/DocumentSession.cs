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
    /// <summary>The selection in the order it was made (first / last selected object for aligning).</summary>
    private readonly List<Entity> _order = [];

    public DocumentSession(CadDocument document) => Document = document;

    public CadDocument Document { get; private set; }

    /// <summary>Serialises/deserialises a document for undo snapshots (set by the IO layer).</summary>
    public Func<CadDocument, string>? Snapshot { get; init; }
    public Func<string, CadDocument>? Restore { get; init; }

    /// <summary>Called when the undo history is empty again, so that data kept for it can go.</summary>
    public Action? HistoryCleared { get; init; }

    public Dictionary<MachineOp, Toolpath> Toolpaths { get; } = [];

    /// <summary>Selected objects in the order they were selected.</summary>
    public IReadOnlyCollection<Entity> Selection => _order;

    private void AddToSelection(Entity e)
    {
        if (_selection.Add(e)) _order.Add(e);
    }

    private void RemoveFromSelection(Entity e)
    {
        if (_selection.Remove(e)) _order.Remove(e);
    }

    private void ClearSelectionSilently()
    {
        _selection.Clear();
        _order.Clear();
    }

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
        HistoryCleared?.Invoke();
        ClearSelectionSilently();
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

    /// <summary>Several changes made one undo step (one run of a script).</summary>
    public sealed class UndoGroup
    {
        internal int UndoDepth { get; set; } = -1;
    }

    /// <summary>
    /// Like <see cref="Execute"/>, but joins the last step of the group as long as nothing else was done in between
    /// (an edit or an undo by the user starts a new step). The state is recorded first, so that a change that fails
    /// half way can be undone.
    /// </summary>
    public void Execute(UndoGroup group, Action action)
    {
        if (group.UndoDepth != _undo.Count) BeginEdit();
        group.UndoDepth = _undo.Count;
        action();
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
        var selectedIds = _order.Select(e => e.Id).ToList();
        Document = restored;
        ClearSelectionSilently();
        var byId = Document.AllEntities().GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var id in selectedIds)
            if (byId.TryGetValue(id, out var e)) AddToSelection(e);
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
        if (!add && !toggle) ClearSelectionSilently();
        foreach (var e in entities)
        {
            if (toggle && _selection.Contains(e)) RemoveFromSelection(e);
            else AddToSelection(e);
        }
        OnSelectionChanged();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        ClearSelectionSilently();
        OnSelectionChanged();
    }

    public void SelectAll() =>
        Select(Document.Layers.Where(l => l.Visible && !l.Locked).SelectMany(l => l.Entities).Where(e => !e.Hidden));

    public bool IsSelected(Entity e) => _selection.Contains(e);

    private void OnSelectionChanged()
    {
        Inspected = _order.Count == 1 ? _order[0] : Inspected is Entity ? null : Inspected;
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
        foreach (var e in _order.Where(e => e.Layer == null || !Document.Layers.Contains(e.Layer)).ToList()) RemoveFromSelection(e);
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
