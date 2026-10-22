using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.App.Controls;

/// <summary>
/// Reflection-based property editor in the style of a classic desktop property grid:
/// categories, name/value columns, state glyphs for machining parameters, expandable composites.
/// </summary>
public sealed partial class PropertyGrid : DockPanel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), FontSize = 11 };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 2) };
    private readonly ToggleButton _sortToggle = new() { Content = "A-Z", Padding = new Thickness(4, 0), MinHeight = 20, FontSize = 11 };
    private readonly ToggleButton _helpToggle = new() { Content = "?", Padding = new Thickness(6, 0), MinHeight = 20, IsChecked = true, FontSize = 11 };
    private readonly Border _helpPanel;
    private readonly HashSet<string> _expanded = [];
    private object? _target;

    public double NameColumnWidth { get; set; } = 150;

    /// <summary>Raised before a value is changed (used for undo snapshots).</summary>
    public event EventHandler? ValueChanging;

    /// <summary>Raised when the [...] button of a point list property is clicked (target object, property).</summary>
    public event EventHandler<(object Target, PropertyInfo Property)>? CollectionEditRequested;

    /// <summary>Raised after a value was changed.</summary>
    public event EventHandler<string>? ValueChanged;

    public PropertyGrid()
    {
        var bar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(2) };
        buttons.Children.Add(_sortToggle);
        buttons.Children.Add(_helpToggle);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_title);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        _helpPanel = new Border
        {
            Child = _help, Height = 64, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
        };
        SetDock(_helpPanel, Dock.Bottom);
        Children.Add(_helpPanel);

        Children.Add(new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brushes.White });

        _sortToggle.IsCheckedChanged += (_, _) => Rebuild();
        _helpToggle.IsCheckedChanged += (_, _) => _helpPanel.IsVisible = _helpToggle.IsChecked == true;
    }

    public object? SelectedObject
    {
        get => _target;
        set
        {
            if (!ReferenceEquals(_target, value)) _expanded.Clear();
            _target = value;
            Rebuild();
        }
    }

    public void Refresh() => Rebuild();

    // ------------------------------------------------------------------ model

    private sealed class Node
    {
        public required string Key { get; init; }
        public required string Display { get; init; }
        public string Category { get; init; } = "Misc";
        /// <summary>Untranslated category (decides the position of the group).</summary>
        public string CategoryKey { get; init; } = "Misc";
        /// <summary>Position inside the category for machining parameters (<see cref="ParamAttribute.Order"/>).</summary>
        public int? Order { get; init; }
        public string? Description { get; init; }
        public required Type Type { get; init; }
        public required Func<object?> Get { get; init; }
        public Action<object?>? Set { get; init; }
        public ICamParam? Param { get; init; }
        public int Depth { get; init; }
        /// <summary>Fixed list of (value, display text) for a string property, see <see cref="ChoicesAttribute"/>.</summary>
        public IReadOnlyList<KeyValuePair<string, string>>? Choices { get; init; }
        public bool IsComposite => IsCompositeType(Type);
    }

    public static bool IsCompositeType(Type t) =>
        t == typeof(Stock) || t == typeof(NestingSettings) || t == typeof(LeadMove) ||
        t == typeof(HoldingTabSettings) || t == typeof(SideProfile) || t == typeof(Services.GridInfo);

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>"StepOver" → "Step Over": display text (and translation key) of property and enum names.</summary>
    public static string Humanize(string name) => WordBoundary().Replace(name.Replace('_', ' '), " ").Replace("  ", " ");

    /// <summary>English display name of a property (the translation key).</summary>
    public static string DisplayNameOf(PropertyInfo prop) =>
        prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? Humanize(prop.GetCustomAttribute<ParamAttribute>()?.XmlName ?? prop.Name);

    /// <summary>English category of a property (the translation key).</summary>
    public static string CategoryOf(PropertyInfo prop) =>
        prop.GetCustomAttribute<ParamAttribute>()?.Category ?? prop.GetCustomAttribute<CategoryAttribute>()?.Category ?? "Misc";

    private IEnumerable<Node> NodesFor(object target, int depth, string prefix, Action<object>? replaceTarget)
    {
        foreach (var prop in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
            if (prop.Name is "EqualityContract") continue;

            var categoryKey = CategoryOf(prop);
            var category = Tr.T(categoryKey);
            var order = prop.GetCustomAttribute<ParamAttribute>()?.Order;
            var display = Tr.T(DisplayNameOf(prop));
            var desc = prop.GetCustomAttribute<DescriptionAttribute>()?.Description is { } d ? Tr.T(d) : null;
            var choices = prop.GetCustomAttribute<ChoicesAttribute>();
            var key = prefix + prop.Name;

            if (typeof(ICamParam).IsAssignableFrom(prop.PropertyType))
            {
                var param = (ICamParam)prop.GetValue(target)!;
                yield return new Node
                {
                    Key = key, Display = display, Category = category, CategoryKey = categoryKey, Order = order, Description = desc, Type = param.ValueType,
                    Get = () => param.BoxedValue,
                    Set = v =>
                    {
                        param.BoxedValue = v;
                        param.State = ParamState.Value;
                        param.RawXml = null;
                    },
                    Param = param, Depth = depth,
                };
                continue;
            }

            if (prop.PropertyType == typeof(List<int>))
            {
                // Id list (Primitive IDs): edited as text "6, 9, 13"; [...] selects interactively in the drawing.
                var listProp = prop;
                var owner = target;
                yield return new Node
                {
                    Key = key, Display = display, Category = category, CategoryKey = categoryKey, Order = order, Description = desc, Type = prop.PropertyType,
                    Get = () => listProp.GetValue(owner),
                    Set = v =>
                    {
                        if (v is List<int> ids)
                        {
                            var list = (List<int>)listProp.GetValue(owner)!;
                            list.Clear();
                            list.AddRange(ids);
                        }
                        else
                        {
                            CollectionEditRequested?.Invoke(this, (owner, listProp));
                        }
                    },
                    Depth = depth,
                };
                continue;
            }

            if (prop.PropertyType == typeof(List<Vertex>) || prop.PropertyType == typeof(List<Point3>))
            {
                var listProp = prop;
                var owner = target;
                yield return new Node
                {
                    Key = key, Display = display, Category = category, CategoryKey = categoryKey, Order = order, Description = desc, Type = prop.PropertyType,
                    Get = () => listProp.GetValue(owner), Set = _ => CollectionEditRequested?.Invoke(this, (owner, listProp)), Depth = depth,
                };
                continue;
            }
            if (!IsEditableType(prop.PropertyType) && !IsCompositeType(prop.PropertyType)) continue;
            var canWrite = prop.SetMethod is { IsPublic: true };
            Action<object?>? setter = null;
            if (canWrite)
            {
                if (replaceTarget != null && IsRecord(target.GetType()))
                {
                    // Immutable record: clone with the changed member and hand back to the parent.
                    setter = v =>
                    {
                        var clone = CloneRecord(target);
                        prop.SetValue(clone, v);
                        replaceTarget(clone);
                    };
                }
                else
                {
                    setter = v => prop.SetValue(target, v);
                }
            }
            yield return new Node
            {
                Key = key, Display = display, Category = category, CategoryKey = categoryKey, Order = order, Description = desc, Type = prop.PropertyType,
                Get = () => prop.GetValue(target), Set = setter, Depth = depth,
                Choices = choices?.GetChoices(target.GetType()),
            };
        }
    }

    private static bool IsRecord(Type t) => t.GetMethod("<Clone>$") != null;

    private static object CloneRecord(object r) => r.GetType().GetMethod("<Clone>$")!.Invoke(r, null)!;

    public static bool IsEditableType(Type t) =>
        t == typeof(string) || t == typeof(double) || t == typeof(int) || t == typeof(bool) || t.IsEnum ||
        t == typeof(Point3) || t == typeof(Point2) || t == typeof(RgbColor) || t == typeof(OptionalPoint) ||
        t == typeof(Matrix4) || Nullable.GetUnderlyingType(t) == typeof(string);

    // ------------------------------------------------------------------ layout

    private void Rebuild()
    {
        _rows.Children.Clear();
        _help.Text = "";
        if (_target == null)
        {
            _title.Text = "";
            return;
        }
        _title.Text = _target switch
        {
            Entity e => Tr.F("{0} ({1})", Tr.T(e.TypeName), e.Id),
            MachineOp op => $"{Tr.T(op.DisplayType)}: {op.Name}",
            Layer l => Tr.F("Layer: {0}", l.Name),
            Part p => Tr.F("Part: {0}", p.Name),
            CadDocument d => Tr.F("Drawing: {0}", d.Name),
            MachiningOptions => Tr.T("Machining"),
            Services.AppSettings => Tr.T("Configuration"),
            _ => _target.GetType().Name,
        };

        var nodes = NodesFor(_target, 0, "", null).ToList();
        if (_sortToggle.IsChecked == true)
        {
            foreach (var n in nodes.OrderBy(n => n.Display)) AddNode(n);
        }
        else
        {
            var appearance = nodes.Select(n => n.CategoryKey).Distinct().ToList();
            var groups = nodes
                .Select((n, i) => (Node: n, Index: i))
                .GroupBy(x => x.Node.CategoryKey)
                .OrderBy(g => CategoryRank(g.Key, appearance.IndexOf(g.Key)))
                .Select(g => g.OrderBy(x => x.Node.Order ?? -1).ThenBy(x => x.Index).Select(x => x.Node).ToList());
            foreach (var group in groups)
            {
                _rows.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                    Padding = new Thickness(4, 2),
                    Child = new TextBlock { Text = group[0].Category, FontWeight = FontWeight.SemiBold },
                });
                foreach (var n in group) AddNode(n);
            }
        }
    }

    /// <summary>
    /// Category order similar to CamBam: identification first (General), then what is machined and how
    /// (depth, cutting, tabs, lead moves, ...), feeds and machine settings, the tool last. Categories not in
    /// the lists keep their order of appearance between the two groups.
    /// </summary>
    private static readonly string[] TopCategories =
        ["General", "(Main)", "Part", "Depth", "Cutting", "Lathe", "Drilling", "Holding Tabs", "Lead Moves", "3D", "Boundary", "Back Face",
         "NC File", "Machining", "Stock", "Nesting"];

    private static readonly string[] BottomCategories = ["Feedrates", "Spindle", "Options", "G-Code", "Output", "Tool", "Misc"];

    public static int CategoryRank(string category, int appearance)
    {
        var top = Array.IndexOf(TopCategories, category);
        if (top >= 0) return top;
        var bottom = Array.IndexOf(BottomCategories, category);
        if (bottom >= 0) return 10_000 + bottom;
        return 1_000 + appearance;
    }

    private void AddNode(Node node)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{NameColumnWidth},*"),
            MinHeight = 21,
        };
        var nameCell = new DockPanel { Background = Brushes.White };
        var indent = 4 + node.Depth * 12;

        if (node.IsComposite)
        {
            var expanded = _expanded.Contains(node.Key);
            var toggle = new Button
            {
                Content = expanded ? "−" : "+", Padding = new Thickness(0), Width = 14, Height = 14, MinHeight = 14, MinWidth = 14,
                FontSize = 10, Margin = new Thickness(indent - 2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            toggle.Click += (_, _) =>
            {
                if (!_expanded.Remove(node.Key)) _expanded.Add(node.Key);
                Rebuild();
            };
            DockPanel.SetDock(toggle, Dock.Left);
            nameCell.Children.Add(toggle);
            indent = 0;
        }

        if (node.Param != null)
        {
            var glyph = StateGlyph(node);
            DockPanel.SetDock(glyph, Dock.Right);
            nameCell.Children.Add(glyph);
        }

        var name = new TextBlock
        {
            Text = node.Display, Margin = new Thickness(indent, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(name, node.Description ?? node.Display);
        nameCell.Children.Add(name);
        nameCell.PointerPressed += (_, _) => _help.Text = $"{node.Display}\n{node.Description}";

        var editor = CreateEditor(node);
        var editorCell = new Border
        {
            Child = editor,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        Grid.SetColumn(editorCell, 1);
        row.Children.Add(nameCell);
        row.Children.Add(editorCell);

        _rows.Children.Add(new Border
        {
            Child = row,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        });

        if (node.IsComposite && _expanded.Contains(node.Key) && node.Get() is { } value)
        {
            Action<object>? replace = node.Set == null ? null : v => Apply(node, v);
            foreach (var child in NodesFor(value, node.Depth + 1, node.Key + ".", replace)) AddNode(child);
        }
    }

    private Control StateGlyph(Node node)
    {
        var p = node.Param!;
        var text = p.State switch { ParamState.Value => "V", ParamState.Auto => "A", _ => "D" };
        var b = new Button
        {
            Content = text, FontSize = 9, Padding = new Thickness(0), Width = 16, Height = 16, MinHeight = 16, MinWidth = 16,
            Margin = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = p.State == ParamState.Value ? Brushes.Black : Brushes.Gray,
        };
        ToolTip.SetTip(b, Tr.T("Default = inherited from style, Value = explicit, Auto = calculated"));
        var menu = new ContextMenu();
        foreach (var s in Enum.GetValues<ParamState>())
        {
            var item = new MenuItem { Header = Tr.T(s.ToString()), IsChecked = p.State == s, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) =>
            {
                ValueChanging?.Invoke(this, EventArgs.Empty);
                p.State = s;
                ValueChanged?.Invoke(this, node.Key);
                Rebuild();
            };
            menu.Items.Add(item);
        }
        b.ContextMenu = menu;
        b.Click += (_, _) => menu.Open(b);
        return b;
    }

    private void Apply(Node node, object? value)
    {
        if (node.Set == null) return;
        ValueChanging?.Invoke(this, EventArgs.Empty);
        try
        {
            node.Set(value);
        }
        catch (Exception ex)
        {
            _help.Text = Tr.F("Invalid value: {0}", ex.InnerException?.Message ?? ex.Message);
            return;
        }
        ValueChanged?.Invoke(this, node.Key);
        Dispatcher(() => Rebuild());
    }

    private static void Dispatcher(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    // ------------------------------------------------------------------ editors

    private Control CreateEditor(Node node)
    {
        var value = node.Get();
        var t = node.Type;
        var isDefault = node.Param is { State: ParamState.Default };
        var readOnly = node.Set == null;

        if (t == typeof(List<Vertex>) || t == typeof(List<Point3>))
        {
            var count = (value as System.Collections.ICollection)?.Count ?? 0;
            var panel = new DockPanel();
            var button = new Button { Content = "...", Padding = new Thickness(6, 0), MinHeight = 18, Margin = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(button, Tr.T("Edit points"));
            button.Click += (_, _) => node.Set?.Invoke(null);
            DockPanel.SetDock(button, Dock.Right);
            panel.Children.Add(button);
            panel.Children.Add(new TextBlock { Text = Tr.F("({0} points)", count), Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        if (t == typeof(List<int>))
        {
            var panel = new DockPanel();
            var button = new Button { Content = "...", Padding = new Thickness(6, 0), MinHeight = 18, Margin = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(button, Tr.T("Select in the drawing"));
            button.Click += (_, _) => node.Set?.Invoke(null);
            DockPanel.SetDock(button, Dock.Right);
            panel.Children.Add(button);
            var ids = (value as List<int>) ?? [];
            panel.Children.Add(TextEditor(node, string.Join(", ", ids), readOnly, isDefault, s => ParseIds(s)));
            return panel;
        }

        if (node.IsComposite)
            // Summaries are enum names ("None", "Automatic") or values ("10 x 20 x 5", "(none)").
            return new TextBlock { Text = value?.ToString() is { } summary ? Tr.T(Humanize(summary)) : "", Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };

        if (t == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = value is true, IsEnabled = !readOnly, Margin = new Thickness(4, 0), MinHeight = 18, Padding = new Thickness(2, 0) };
            cb.IsCheckedChanged += (_, _) => Apply(node, cb.IsChecked == true);
            return cb;
        }

        if (t.IsEnum || node.Choices != null)
        {
            // (value, display text): enum members are shown humanized and translated ("DepthFirst" → "Depth First").
            var choices = node.Choices ?? Enum.GetNames(t)
                .Select(n => new KeyValuePair<string, string>(n, Tr.T(Humanize(n)))).ToList();
            var current = value?.ToString() ?? "";
            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Value).ToList(),
                SelectedIndex = choices.ToList().FindIndex(c => c.Key == current), IsEnabled = !readOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 20, Padding = new Thickness(4, 0),
                BorderThickness = new Thickness(0), FontStyle = isDefault ? FontStyle.Italic : FontStyle.Normal,
                Foreground = isDefault ? Brushes.Gray : Brushes.Black,
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                var selected = choices[combo.SelectedIndex].Key;
                if (selected != current) Apply(node, t.IsEnum ? Enum.Parse(t, selected) : selected);
            };
            return combo;
        }

        if (t == typeof(RgbColor) && value is RgbColor color)
        {
            var panel = new DockPanel();
            var swatch = new Border
            {
                Width = 18, Height = 12, Margin = new Thickness(4, 0), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            };
            DockPanel.SetDock(swatch, Dock.Left);
            panel.Children.Add(swatch);
            panel.Children.Add(TextEditor(node, color.ToString(), readOnly, isDefault, s =>
            {
                var parts = s.Split(',');
                if (parts.Length < 3) throw new FormatException(Tr.T("Expected r,g,b"));
                return new RgbColor(byte.Parse(parts[0].Trim(), Inv), byte.Parse(parts[1].Trim(), Inv), byte.Parse(parts[2].Trim(), Inv));
            }));
            return panel;
        }

        string text = value switch
        {
            null => "",
            double d => d.ToString("0.######", Inv),
            Point3 p => $"{F(p.X)},{F(p.Y)},{F(p.Z)}",
            Point2 p => $"{F(p.X)},{F(p.Y)}",
            OptionalPoint op => op.Point is { } pp ? $"{F(pp.X)},{F(pp.Y)},{F(pp.Z)}" : "",
            Matrix4 m => m.IsIdentity ? Tr.T("Identity") : string.Join(" ", m.ToArray().Select(F)),
            _ => Convert.ToString(value, Inv) ?? "",
        };

        if (t == typeof(Matrix4)) readOnly = true;

        Func<string, object?> parse = t switch
        {
            _ when t == typeof(double) => s => double.Parse(s, NumberStyles.Float, Inv),
            _ when t == typeof(int) => s => int.Parse(s, NumberStyles.Integer, Inv),
            _ when t == typeof(Point3) => s => ParsePoint(s),
            _ when t == typeof(Point2) => s => ParsePoint(s).XY,
            _ when t == typeof(OptionalPoint) => s => string.IsNullOrWhiteSpace(s) ? OptionalPoint.None : new OptionalPoint(ParsePoint(s)),
            _ => s => s,
        };
        return TextEditor(node, text, readOnly, isDefault, parse);
    }

    private static string F(double v) => v.ToString("0.######", Inv);

    /// <summary>"6, 9, 13" (also separated by spaces or semicolons, optionally in braces) → ids.</summary>
    public static List<int> ParseIds(string s)
    {
        var ids = new List<int>();
        foreach (var part in s.Split([',', ';', ' ', '\t', '{', '}', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, Inv, out var id) || id < 0)
                throw new FormatException(Tr.T("Expected object IDs separated by commas, e.g. 6, 9, 13"));
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    private static Point3 ParsePoint(string s)
    {
        var parts = s.Split(',', ';');
        double P(int i) => i < parts.Length && parts[i].Trim().Length > 0 ? double.Parse(parts[i].Trim(), NumberStyles.Float, Inv) : 0;
        if (parts.Length < 2) throw new FormatException(Tr.T("Expected x,y[,z]"));
        return new Point3(P(0), P(1), P(2));
    }

    private Control TextEditor(Node node, string text, bool readOnly, bool isDefault, Func<string, object?> parse)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = readOnly, Classes = { "cell" },
            FontStyle = isDefault ? FontStyle.Italic : FontStyle.Normal,
            Foreground = readOnly || isDefault ? Brushes.Gray : Brushes.Black,
        };
        void Commit()
        {
            if (readOnly || box.Text == text) return;
            try
            {
                Apply(node, parse(box.Text ?? ""));
            }
            catch (Exception ex)
            {
                _help.Text = Tr.F("Invalid value for {0}: {1}", node.Display, ex.Message);
                box.Text = text;
            }
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = text;
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
        box.GotFocus += (_, _) => _help.Text = $"{node.Display}\n{node.Description}";
        return box;
    }
}
