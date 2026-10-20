using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.App.Drawing;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private bool _syncingTree;
    private readonly HashSet<string> _collapsed = [];
    private readonly Dictionary<object, TreeViewItem> _treeItems = new(ReferenceEqualityComparer.Instance);

    private static string NodeKey(object o) => o switch
    {
        Layer l => "L:" + l.Name,
        Part p => "P:" + p.Name,
        string s => "S:" + s,
        _ => o.GetType().Name,
    };

    private TreeViewItem Node(object tag, string text, IBrush? swatch = null, bool dimmed = false, bool bold = false, bool expandable = false)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (swatch != null)
            header.Children.Add(new Rectangle { Width = 10, Height = 10, Fill = swatch, Stroke = Brushes.Gray, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock
        {
            Text = text, Foreground = dimmed ? Brushes.Gray : Brushes.Black,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center,
        });
        var item = new TreeViewItem { Header = header, Tag = tag };
        if (expandable)
        {
            var key = NodeKey(tag);
            item.IsExpanded = !_collapsed.Contains(key);
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (item.IsExpanded) _collapsed.Remove(key);
                else _collapsed.Add(key);
            };
        }
        item.ContextMenu = ContextMenuFor(tag);
        _treeItems[tag] = item;
        return item;
    }

    private void RebuildTree()
    {
        _syncingTree = true;
        _treeItems.Clear();
        var doc = _session.Document;
        var root = Node(doc, doc.Name, bold: true, expandable: true);

        var layers = Node("Layers", "Layers", expandable: true);
        foreach (var layer in doc.Layers)
        {
            var active = layer.Name == doc.ActiveLayerName;
            var text = layer.Name + (active ? "  ◄" : "") + (layer.Locked ? "  (locked)" : "");
            var c = layer.Color;
            var ln = Node(layer, text, new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), dimmed: !layer.Visible, expandable: true);
            // Large layers: avoid building thousands of nodes.
            foreach (var e in layer.Entities.Take(2000)) ln.Items.Add(Node(e, $"{e.TypeName} ({e.Id})"));
            if (layer.Entities.Count > 2000) ln.Items.Add(new TreeViewItem { Header = $"… {layer.Entities.Count - 2000} more" });
            layers.Items.Add(ln);
        }
        root.Items.Add(layers);

        var machining = Node(doc.Machining, "Machining", expandable: true);
        foreach (var part in doc.Parts)
        {
            var active = part.Name == doc.ActivePartName;
            var pn = Node(part, part.Name + (active ? "  ◄" : ""), dimmed: !part.Enabled, expandable: true);
            foreach (var op in part.Operations)
            {
                var hasPath = _session.Toolpaths.ContainsKey(op);
                pn.Items.Add(Node(op, op.Name + (op is UnsupportedOp ? $"  [{op.XmlName}]" : ""), dimmed: !op.Enabled || !part.Enabled, bold: hasPath));
            }
            machining.Items.Add(pn);
        }
        root.Items.Add(machining);

        DrawingTree.ItemsSource = null;
        DrawingTree.Items.Clear();
        DrawingTree.Items.Add(root);
        _syncingTree = false;
        SyncTreeSelection();
    }

    private void SyncTreeSelection()
    {
        if (_syncingTree) return;
        _syncingTree = true;
        try
        {
            var wanted = _session.Selection.Count > 0
                ? _session.Selection.Cast<object>().ToList()
                : _session.Inspected != null ? [_session.Inspected] : [];
            DrawingTree.SelectedItems.Clear();
            foreach (var o in wanted)
                if (_treeItems.TryGetValue(o, out var item))
                {
                    DrawingTree.SelectedItems.Add(item);
                    if (wanted.Count == 1) item.BringIntoView();
                }
        }
        finally
        {
            _syncingTree = false;
        }
        View.Invalidate();
    }

    private IEnumerable<object> SelectedTags() =>
        DrawingTree.SelectedItems.OfType<TreeViewItem>().Select(i => i.Tag).Where(t => t != null)!;

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree) return;
        _syncingTree = true;
        try
        {
            var tags = SelectedTags().ToList();
            var entities = tags.OfType<Entity>().ToList();
            if (entities.Count > 0)
            {
                _session.Select(entities);
            }
            else if (tags.FirstOrDefault() is { } tag && tag is not string)
            {
                _session.ClearSelection();
                if (tag is Layer l) _session.Document.ActiveLayerName = l.Name;
                if (tag is Part p) _session.Document.ActivePartName = p.Name;
                if (tag is MachineOp op)
                {
                    var part = _session.Document.Parts.FirstOrDefault(x => x.Operations.Contains(op));
                    if (part != null) _session.Document.ActivePartName = part.Name;
                    _clipboardOp ??= null;
                }
                _session.Inspect(tag);
            }
        }
        finally
        {
            _syncingTree = false;
        }
        View.Invalidate();
    }

    private Layer? SelectedTreeLayer() => SelectedTags().OfType<Layer>().FirstOrDefault();

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space when e.KeyModifiers == KeyModifiers.Control:
                foreach (var l in SelectedTags().OfType<Layer>()) l.Locked = !l.Locked;
                _session.NotifyChanged();
                e.Handled = true;
                break;
            case Key.Space:
                _session.Execute("Toggle", () =>
                {
                    foreach (var t in SelectedTags())
                    {
                        if (t is Layer l) l.Visible = !l.Visible;
                        else if (t is Part p) p.Enabled = !p.Enabled;
                        else if (t is MachineOp op) op.Enabled = !op.Enabled;
                    }
                });
                e.Handled = true;
                break;
            case Key.Delete:
                if (DeleteTreeItem()) e.Handled = true;
                break;
            case Key.F2:
                _ = RenameSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SelectedTags().FirstOrDefault() is TextEntity t) _ = EditTextEntity(t);
    }

    private async Task EditTextEntity(TextEntity t)
    {
        var text = await Dialogs.EditText(this, "Text", t.Text);
        if (text == null) return;
        _session.Execute("Edit text", () =>
        {
            t.Text = text;
            t.InvalidateOutlines();
        });
    }

    /// <summary>Deletes selected layers, parts or operations; returns false if no such item was selected.</summary>
    private bool DeleteTreeItem()
    {
        var tags = SelectedTags().ToList();
        if (!tags.Any(t => t is Layer or Part or MachineOp)) return false;
        var doc = _session.Document;
        _session.Execute("Delete", () =>
        {
            foreach (var t in tags)
            {
                switch (t)
                {
                    case Layer l when doc.Layers.Count > 1:
                        doc.Layers.Remove(l);
                        if (doc.ActiveLayerName == l.Name) doc.ActiveLayerName = doc.Layers[0].Name;
                        break;
                    case Part p:
                        doc.Parts.Remove(p);
                        foreach (var op in p.Operations) _session.Toolpaths.Remove(op);
                        break;
                    case MachineOp op:
                        foreach (var p in doc.Parts) p.Operations.Remove(op);
                        _session.Toolpaths.Remove(op);
                        break;
                }
            }
        });
        _session.Inspect(null);
        return true;
    }

    private async Task RenameSelected()
    {
        var tag = SelectedTags().FirstOrDefault() ?? _session.Inspected;
        string? current = tag switch
        {
            Layer l => l.Name,
            Part p => p.Name,
            MachineOp op => op.Name,
            CadDocument d => d.Name,
            _ => null,
        };
        if (current == null) return;
        var name = await Dialogs.Prompt(this, "Rename", "New name:", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;
        var doc = _session.Document;
        _session.Execute("Rename", () =>
        {
            switch (tag)
            {
                case Layer l:
                    if (doc.ActiveLayerName == l.Name) doc.ActiveLayerName = name;
                    l.Name = name;
                    break;
                case Part p:
                    if (doc.ActivePartName == p.Name) doc.ActivePartName = name;
                    p.Name = name;
                    break;
                case MachineOp op: op.Name = name; break;
                case CadDocument d: d.Name = name; break;
            }
        });
    }

    private ContextMenu? ContextMenuFor(object tag)
    {
        var doc = _session.Document;
        var menu = new ContextMenu();
        void Add(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        void Sep() => menu.Items.Add(new Separator());

        switch (tag)
        {
            case "Layers":
            case Layer:
                var layer = tag as Layer;
                Add("New Layer", () =>
                {
                    var l = new Layer(doc.UniqueLayerName());
                    _session.Execute("New layer", () =>
                    {
                        doc.Layers.Add(l);
                        doc.ActiveLayerName = l.Name;
                    });
                });
                if (layer == null) break;
                Add("Set As Active Layer", () => { doc.ActiveLayerName = layer.Name; _session.NotifyChanged(); });
                Sep();
                Add(layer.Visible ? "Hide" : "Show", () => _session.Execute("Toggle layer", () => layer.Visible = !layer.Visible));
                Add("Hide All But This", () => _session.Execute("Hide layers", () => { foreach (var l in doc.Layers) l.Visible = l == layer; }));
                Add("Show All", () => _session.Execute("Show layers", () => { foreach (var l in doc.Layers) l.Visible = true; }));
                Add(layer.Locked ? "Unlock" : "Lock", () => _session.Execute("Lock layer", () => layer.Locked = !layer.Locked));
                Sep();
                Add("Select All On Layer", () => _session.Select(layer.Entities));
                Add("Clear", () => _session.Execute("Clear layer", () => { foreach (var e in layer.Entities.ToList()) doc.RemoveEntity(e); }));
                Add("Paste", () => _ = Paste());
                Add("Rename", () => _ = RenameSelected());
                Add("Delete", () => { if (doc.Layers.Count > 1) _session.Execute("Delete layer", () => doc.Layers.Remove(layer)); });
                break;

            case MachiningOptions:
                Add("Generate Toolpaths", () => GenerateToolpaths());
                Add("Produce Gcode", () => _ = ProduceGCode(PostScope.All));
                Add("Edit Gcode", EditGCode);
                Sep();
                Add("New Part", NewPart);
                Add("Set Machining Origin", () => View.SetTool(new PickPointTool("Select machining origin",
                    p => _session.Execute("Set origin", () => doc.Machining.MachiningOrigin = new OptionalPoint(p.ToPoint3())))));
                break;

            case Part part:
                Add("Generate Toolpaths", () => GenerateToolpaths(new PostScope(part)));
                Add("Produce Gcode", () => _ = ProduceGCode(new PostScope(part)));
                Sep();
                Add(part.Enabled ? "Disable" : "Enable", () => _session.Execute("Toggle part", () => part.Enabled = !part.Enabled));
                Add("Set As Active Part", () => { doc.ActivePartName = part.Name; _session.NotifyChanged(); });
                Add("Rename", () => _ = RenameSelected());
                Add("Delete", () => _session.Execute("Delete part", () => doc.Parts.Remove(part)));
                break;

            case MachineOp op:
                Add("Generate Toolpaths", () => GenerateToolpaths(new PostScope(Operation: op)));
                Add("Produce Gcode", () => _ = ProduceGCode(new PostScope(doc.Parts.FirstOrDefault(p => p.Operations.Contains(op)), op)));
                Sep();
                Add(op.Enabled ? "Disable MOP" : "Enable MOP", () => _session.Execute("Toggle MOP", () => op.Enabled = !op.Enabled));
                Add("Select Drawing Objects", () =>
                {
                    if (_session.Selection.Count == 0)
                    {
                        _session.Select(op.PrimitiveIds.Select(doc.FindEntity).OfType<Entity>());
                        Log("Source objects selected. Change the selection, then use 'Select Drawing Objects' again to apply.");
                    }
                    else
                    {
                        var ids = _session.Selection.Select(e => e.Id).ToList();
                        _session.Execute("Set MOP objects", () =>
                        {
                            op.PrimitiveIds.Clear();
                            op.PrimitiveIds.AddRange(ids);
                        });
                        _session.Inspect(op);
                        Log($"{op.Name}: {ids.Count} drawing object(s).");
                    }
                });
                Add("Set Start Point", () => View.SetTool(new PickPointTool("Select start point",
                    p => _session.Execute("Set start point", () => op.StartPoint.Set(new OptionalPoint(p.ToPoint3()))))));
                Add("Copy", () => _clipboardOp = op.CloneOp());
                Add("Paste Format", () => { _session.Inspect(op); PasteFormat(); });
                Add("Duplicate", () =>
                {
                    var part = doc.Parts.First(p => p.Operations.Contains(op));
                    var clone = op.CloneOp();
                    clone.Name = UniqueOpName(op.DisplayType.Replace(" ", ""));
                    _session.Execute("Duplicate MOP", () => part.Operations.Insert(part.Operations.IndexOf(op) + 1, clone));
                });
                Add("Move Up", () => MoveOp(op, -1));
                Add("Move Down", () => MoveOp(op, 1));
                Add("Reset To Defaults", () => _session.Execute("Reset MOP", op.ResetToDefaults));
                Add("Toolpaths To Geometry", () => ToolpathToGeometry(op));
                Sep();
                Add("Rename", () => _ = RenameSelected());
                Add("Delete", () => _session.Execute("Delete MOP", () => { foreach (var p in doc.Parts) p.Operations.Remove(op); }));
                break;

            case Entity e:
                Add("Copy", () => _ = CopySelection());
                Add("Cut", () => _ = CutSelection());
                Add("Delete", DeleteSelection);
                if (e is TextEntity t) Add("Edit Text", () => _ = EditTextEntity(t));
                break;

            default:
                return null;
        }
        return menu;
    }

    private void MoveOp(MachineOp op, int delta)
    {
        var part = _session.Document.Parts.First(p => p.Operations.Contains(op));
        var i = part.Operations.IndexOf(op);
        var j = Math.Clamp(i + delta, 0, part.Operations.Count - 1);
        if (i == j) return;
        _session.Execute("Move MOP", () => part.Operations.Move(i, j));
    }

    private void ToolpathToGeometry(MachineOp op)
    {
        if (!_session.Toolpaths.TryGetValue(op, out var tp))
        {
            Log("Generate toolpaths first.");
            return;
        }
        var verts = new List<Vertex>();
        var created = new List<PolylineEntity>();
        Point3? pos = null;
        void Flush()
        {
            if (verts.Count > 1) created.Add(new PolylineEntity(new Path2(verts.ToList(), false)));
            verts.Clear();
        }
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (m.Kind == MoveKind.Rapid)
            {
                Flush();
                pos = m.To;
                continue;
            }
            if (verts.Count == 0 && pos is { } p) verts.Add(new Vertex(p));
            if (m.IsArc && verts.Count > 0)
            {
                var from = verts[^1].Point;
                var a0 = (from.XY - m.Center).Angle;
                var a1 = (m.To.XY - m.Center).Angle;
                var sweep = m.Kind == MoveKind.ArcCCW ? Angles.Normalize(a1 - a0) : -Angles.Normalize(a0 - a1);
                if (Math.Abs(sweep) < 1e-9) sweep = m.Kind == MoveKind.ArcCCW ? Angles.TwoPi : -Angles.TwoPi;
                // Split into halves to keep bulges well-conditioned.
                var r = Point2.Distance(from.XY, m.Center);
                var mid = m.Center + Point2.FromAngle(a0 + sweep / 2, r);
                verts[^1] = verts[^1] with { Bulge = Math.Tan(sweep / 8) };
                verts.Add(new Vertex(mid.ToPoint3((from.Z + m.To.Z) / 2), Math.Tan(sweep / 8)));
            }
            verts.Add(new Vertex(m.To));
            pos = m.To;
        }
        Flush();
        _session.Execute("Toolpaths to geometry", () => { foreach (var c in created) _session.Document.AddEntity(c); });
        Log($"{created.Count} polyline(s) created from {op.Name}.");
    }

    private void BuildSystemTree()
    {
        SystemTree.Items.Clear();
        var posts = new TreeViewItem { Header = "Post Processors", IsExpanded = true };
        foreach (var p in _system.Posts)
        {
            var src = _system.PostSources.GetValueOrDefault(p);
            var item = new TreeViewItem
            {
                Header = p.Name + (p.Name == _system.DefaultPostName ? "  ◄" : "") + (src == null ? "  (built-in)" : ""),
                Tag = p,
            };
            ToolTip.SetTip(item, src ?? "Built into BamCam");
            var menu = new ContextMenu();
            var setDefault = new MenuItem { Header = "Set As Default" };
            setDefault.Click += (_, _) => { _system.DefaultPostName = p.Name; BuildSystemTree(); };
            menu.Items.Add(setDefault);
            item.ContextMenu = menu;
            posts.Items.Add(item);
        }
        SystemTree.Items.Add(posts);

        var styles = new TreeViewItem { Header = "CAM Styles", IsExpanded = true };
        foreach (var lib in CamLibraries.StyleLibraries)
        {
            var li = new TreeViewItem { Header = lib.Name, Tag = lib };
            ToolTip.SetTip(li, lib.SourcePath);
            foreach (var s in lib.Styles) li.Items.Add(new TreeViewItem { Header = s.ToString() + (s.ParentStyle.Length > 0 ? $"  ← {s.ParentStyle}" : ""), Tag = s });
            styles.Items.Add(li);
        }
        SystemTree.Items.Add(styles);

        var tools = new TreeViewItem { Header = "Tools", IsExpanded = true };
        foreach (var lib in CamLibraries.ToolLibraries)
        {
            var li = new TreeViewItem { Header = lib.Name, Tag = lib };
            ToolTip.SetTip(li, lib.SourcePath);
            foreach (var t in lib.Tools.OrderBy(t => t.Index)) li.Items.Add(new TreeViewItem { Header = t.ToString(), Tag = t });
            tools.Items.Add(li);
        }
        SystemTree.Items.Add(tools);
    }

    private void OnSystemTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var tag = (SystemTree.SelectedItem as TreeViewItem)?.Tag;
        if (tag is PostProcessorDefinition or ToolDefinition) Properties.SelectedObject = tag;
    }

    private void ShowViewContextMenu(Point p)
    {
        var menu = new ContextMenu();
        void Add(ItemsControl parent, string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            parent.Items.Add(mi);
        }
        var hasSel = _session.Selection.Count > 0;
        if (hasSel)
        {
            Add(menu, "Cut", () => _ = CutSelection());
            Add(menu, "Copy", () => _ = CopySelection());
        }
        Add(menu, "Paste", () => _ = Paste());
        if (hasSel)
        {
            Add(menu, "Delete", DeleteSelection);
            menu.Items.Add(new Separator());
            var edit = new MenuItem { Header = "Edit" };
            Add(edit, "Join...", Join);
            Add(edit, "Offset...", () => Offset(false));
            Add(edit, "Explode", Explode);
            Add(edit, "Union", () => BooleanOp("Union"));
            Add(edit, "Subtract", () => BooleanOp("Subtract"));
            Add(edit, "Intersection", () => BooleanOp("Intersection"));
            Add(edit, "Convert To Polyline", ConvertToPolyline);
            Add(edit, "Convert To Region", ConvertToRegion);
            Add(edit, "Reverse", ReversePolylines);
            menu.Items.Add(edit);
            var tr = new MenuItem { Header = "Transform" };
            Add(tr, "Move", () => View.SetTool(new MoveTool()));
            Add(tr, "Rotate", () => View.SetTool(new RotateTool()));
            Add(tr, "Resize...", Resize);
            Add(tr, "Mirror", () => View.SetTool(new MirrorTool()));
            Add(tr, "Array Copy...", ArrayCopy);
            Add(tr, "Center (Extents)", CenterExtents);
            menu.Items.Add(tr);
            var cam = new MenuItem { Header = "Machining" };
            Add(cam, "Profile", () => CreateOp(new ProfileOp()));
            Add(cam, "Pocket", () => CreateOp(new PocketOp()));
            Add(cam, "Drill", () => CreateOp(new DrillOp()));
            Add(cam, "Engrave", () => CreateOp(new EngraveOp()));
            menu.Items.Add(cam);
        }
        menu.Items.Add(new Separator());
        var view = new MenuItem { Header = "View" };
        Add(view, "Zoom To Fit", () => View.ZoomToFit());
        Add(view, "Reset", () => View.ResetView());
        Add(view, "Snap To Grid", () => { View.Settings.SnapToGrid = !View.Settings.SnapToGrid; RefreshChecks(); UpdateTitle(); });
        Add(view, "Snap To Objects", () => { View.Settings.SnapToObjects = !View.Settings.SnapToObjects; RefreshChecks(); UpdateTitle(); });
        menu.Items.Add(view);
        var gen = new MenuItem { Header = "Generate Toolpaths" };
        gen.Click += (_, _) => GenerateToolpaths();
        menu.Items.Add(gen);
        if (_session.Inspected is ProfileOp)
        {
            menu.Items.Add(new Separator());
            var tabs = new MenuItem { Header = "Holding Tabs" };
            Add(tabs, "Autocalc", View.AutocalcTabs);
            Add(tabs, "Add Tab", () => View.AddTabAt(p));
            var remove = new MenuItem { Header = "Remove Tab", IsEnabled = View.TabAt(p) != null };
            remove.Click += (_, _) => View.RemoveTabAt(p);
            tabs.Items.Add(remove);
            menu.Items.Add(tabs);
        }
        menu.Open(View);
    }
}
