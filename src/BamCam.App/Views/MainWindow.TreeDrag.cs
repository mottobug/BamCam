using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using BamCam.Core.Cam;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Drag and drop in the drawing tree: drawing objects onto another layer (or onto an object of it), machining
/// operations onto a part (to its end) or onto another operation (before or after it, also in another part).
/// </summary>
public partial class MainWindow
{
    private const string TreeDragFormat = "application/x-bamcam-tree";
    private Point? _treeDragStart;
    private TreeViewItem? _treeDragItem;
    private List<object> _treeDragTags = [];
    private TreeViewItem? _treeDropItem;
    private IBrush? _treeDropBackground;

    private void SetupTreeDragDrop()
    {
        DrawingTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DrawingTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        DrawingTree.AddHandler(PointerReleasedEvent, (_, _) => _treeDragStart = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(DrawingTree, true);
        DrawingTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        DrawingTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => MarkDropTarget(null));
        DrawingTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
    }

    private static TreeViewItem? ItemAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _treeDragStart = null;
        if (!e.GetCurrentPoint(DrawingTree).Properties.IsLeftButtonPressed) return;
        if (ItemAt(e.Source) is not { Tag: Entity or MachineOp } item) return;
        _treeDragStart = e.GetPosition(DrawingTree);
        _treeDragItem = item;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treeDragStart is not { } start || _treeDragItem is not { } item) return;
        if (!e.GetCurrentPoint(DrawingTree).Properties.IsLeftButtonPressed)
        {
            _treeDragStart = null;
            return;
        }
        var d = e.GetPosition(DrawingTree) - start;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        _treeDragStart = null;

        // The pressed object, or all selected objects of the same kind when it is part of the selection.
        var tags = SelectedTags().ToList();
        _treeDragTags = item.Tag switch
        {
            Entity when tags.Contains(item.Tag) => tags.OfType<Entity>().Cast<object>().ToList(),
            MachineOp when tags.Contains(item.Tag) => tags.OfType<MachineOp>().Cast<object>().ToList(),
            _ => [item.Tag!],
        };
#pragma warning disable CS0618 // DataObject: the drag only names its kind; the objects are kept here
        var data = new DataObject();
        data.Set(TreeDragFormat, item.Tag is Entity ? "entities" : "operations");
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        MarkDropTarget(null);
        _treeDragTags = [];
    }

    /// <summary>Where the dragged objects would go, or null when they cannot be dropped there.</summary>
    private (Layer? Layer, Part? Part, int Index)? DropTarget(DragEventArgs e)
    {
        if (_treeDragTags.Count == 0 || ItemAt(e.Source) is not { } item) return null;
        var doc = _session.Document;
        if (_treeDragTags[0] is Entity)
        {
            var layer = item.Tag switch
            {
                Layer l => l,
                Entity target => doc.Layers.FirstOrDefault(l => l.Entities.Contains(target)),
                _ => null,
            };
            return layer == null || layer.Locked ? null : (layer, null, 0);
        }
        switch (item.Tag)
        {
            case Part part:
                return (null, part, part.Operations.Count);
            case MachineOp target when !_treeDragTags.Contains(target):
            {
                var part = doc.Parts.First(p => p.Operations.Contains(target));
                // Upper half of the row: before the operation, lower half: after it.
                var y = e.GetPosition(item).Y; // operation rows have no children: the item is one row
                var after = y > item.Bounds.Height / 2;
                return (null, part, part.Operations.IndexOf(target) + (after ? 1 : 0));
            }
            default:
                return null;
        }
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        e.DragEffects = target == null ? DragDropEffects.None : DragDropEffects.Move;
        MarkDropTarget(target == null ? null : ItemAt(e.Source));
        e.Handled = true;
    }

    /// <summary>Highlights the row the objects would be dropped on.</summary>
    private void MarkDropTarget(TreeViewItem? item)
    {
        if (_treeDropItem == item) return;
        if (_treeDropItem != null) _treeDropItem.Background = _treeDropBackground;
        _treeDropItem = item;
        if (item == null) return;
        _treeDropBackground = item.Background;
        item.Background = new SolidColorBrush(Color.FromArgb(70, 0, 120, 215));
    }

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        MarkDropTarget(null);
        e.Handled = true;
        if (target is not { } t) return;
        var (layer, part, index) = t;
        var doc = _session.Document;
        if (layer != null)
        {
            var entities = _treeDragTags.OfType<Entity>().Where(x => !layer.Entities.Contains(x)).ToList();
            if (entities.Count == 0) return;
            _session.Execute(F("Move to layer {0}", layer.Name), () =>
            {
                foreach (var entity in entities) doc.MoveEntity(entity, layer);
            });
            Log(F("{0} object(s) moved to layer {1}.", entities.Count, layer.Name));
            return;
        }
        if (part == null) return;
        var ops = _treeDragTags.OfType<MachineOp>().ToList();
        _session.Execute(T("Move MOP"), () => doc.MoveOperations(ops, part, index));
    }
}
