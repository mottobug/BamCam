using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BamCam.App.Controls;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Simulation;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// 3D simulation of the milling operations on the stock (height field), with the material left above the target
/// depth of each operation shown in colour, or of the turning operations (the stock section cut move by move and
/// turned about the spindle axis). The moves can be stepped one by one, forwards and backwards.
/// </summary>
public sealed class SimulationWindow : Window
{
    private readonly CadDocument _doc;
    private readonly IReadOnlyDictionary<MachineOp, Toolpath> _toolpaths;
    private readonly List<(MachineOp Op, CheckBox Box)> _ops = [];
    private readonly SimulationView _view = new();
    private readonly Slider _slider = new()
    {
        Minimum = 0, Maximum = 1, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _progress = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _current = new() { FontSize = 11, Foreground = Brushes.DimGray };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _cell = new() { Width = 80 };
    private readonly TextBox _tolerance = new() { Width = 80 };
    private readonly Button _play = new() { Content = "▷", MinWidth = 36 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ComboBox _speed = new() { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly Stopwatch _tickWatch = new();
    private double _due; // moves owed to the playing speed

    /// <summary>Playing speeds in moves per second (0 = as fast as the simulation runs).</summary>
    private static readonly int[] Speeds = [1, 2, 5, 10, 25, 50, 100, 250, 1000, 0];

    // Milling: a height field cut by the moves.
    private HeightField? _field;
    private List<MillingSimulation.SimMove> _moves = [];

    // Turning: the stock section cut by the moves, shown turned about the axis.
    private bool _turning;
    private TurningSimulation.Frame? _frame;
    private List<TurningSimulation.TurnMove> _turnMoves = [];
    private List<Path2> _section = [];

    private int _done; // moves applied
    private int _generation; // counts restarts, so that a simulation running meanwhile is dropped
    private bool _busy, _pending, _updatingSlider;

    /// <summary>Copies of the material after every n-th move, so that stepping back does not start from the beginning.</summary>
    private readonly SortedDictionary<int, object> _checkpoints = [];
    private int _checkpointEvery = int.MaxValue;

    private readonly Action<SurfaceEntity>? _addSurface;
    private string _cellNote = "";
    private readonly Control _cellRow, _toleranceRow, _exports, _millLegend, _turnLegend;

    private int Count => _turning ? _turnMoves.Count : _moves.Count;

    /// <param name="addSurface">Adds the simulated material to the drawing (one undo step); null hides the button.</param>
    public SimulationWindow(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths, Action<SurfaceEntity>? addSurface = null)
    {
        _doc = doc;
        _toolpaths = toolpaths;
        _addSurface = addSurface;
        Title = T("3D Simulation");
        Width = 1100;
        Height = 740;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var left = new StackPanel { Margin = new Thickness(10), Spacing = 6, Width = 250 };
        left.Children.Add(new TextBlock { Text = T("Operations"), FontWeight = FontWeight.SemiBold });
        left.Children.Add(_opsPanel);
        BuildOperations(doc.Parts.SelectMany(p => p.Operations).Where(o => o.Enabled).ToList());

        left.Children.Add(_cellRow = Row(T("Cell size"), _cell));
        left.Children.Add(_toleranceRow = Row(T("Tolerance"), _tolerance));
        var apply = new Button { Content = T("Simulate"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        apply.Click += (_, _) => Restart();
        left.Children.Add(apply);
        // The material as it is now, at the move shown: a surface for the drawing or an STL file.
        var exports = new StackPanel { Spacing = 6 };
        if (_addSurface != null)
        {
            var add = new Button { Content = T("Add to Drawing"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(add, T("Adds the simulated material as a surface on a new layer."));
            add.Click += (_, _) => AddToDrawing();
            exports.Children.Add(add);
        }
        var stl = new Button { Content = T("Save STL..."), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(stl, T("Saves the simulated material as an STL mesh."));
        stl.Click += (_, _) => SaveStl();
        exports.Children.Add(stl);
        left.Children.Add(_exports = exports);
        left.Children.Add(_millLegend = Legend(turning: false));
        left.Children.Add(_turnLegend = Legend(turning: true));
        left.Children.Add(_info);
        left.Children.Add(new TextBlock
        {
            Text = T("Left drag: rotate, right drag: move, wheel: zoom, double click: reset. Arrow keys: previous / next move, Home / End: start / end, Space: play."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray,
        });
        ShowMode();

        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingSlider) RunTo((int)Math.Round(_slider.Value));
        };
        _play.Click += (_, _) => TogglePlay();
        ToolTip.SetTip(_play, T("Play / pause (Space)"));
        _speed.ItemsSource = Speeds.Select(s => s > 0 ? F("{0} moves/s", s) : T("Maximum")).ToList();
        _speed.SelectedIndex = Array.IndexOf(Speeds, 10);
        ToolTip.SetTip(_speed, T("Speed of playing"));
        _timer.Tick += (_, _) =>
        {
            var elapsed = _tickWatch.Elapsed.TotalSeconds;
            _tickWatch.Restart();
            if (_busy) return;
            if (_done >= Count)
            {
                TogglePlay();
                return;
            }
            var speed = Speeds[Math.Max(0, _speed.SelectedIndex)];
            if (speed <= 0)
            {
                RunTo(_done + Math.Max(1, Count / 100));
                return;
            }
            // Moves due since the last tick (at slow speeds one every few ticks).
            _due = Math.Min(_due + speed * elapsed, Math.Max(1, speed / 5.0));
            var n = (int)_due;
            if (n < 1) return;
            _due -= n;
            RunTo(_done + n);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(StepButton("|◀", T("To the start (Home)"), () => RunTo(0)));
        buttons.Children.Add(StepButton("◀", T("Previous move (Left)"), () => RunTo(_done - 1)));
        buttons.Children.Add(_play);
        buttons.Children.Add(StepButton("▶", T("Next move (Right)"), () => RunTo(_done + 1)));
        buttons.Children.Add(StepButton("▶|", T("To the end (End)"), () => RunTo(Count)));
        buttons.Children.Add(_speed);

        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), MinWidth = 260 };
        status.Children.Add(_progress);
        status.Children.Add(_current);
        var bottom = new DockPanel { Margin = new Thickness(10, 4, 10, 10) };
        DockPanel.SetDock(buttons, Dock.Left);
        DockPanel.SetDock(status, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(status);
        bottom.Children.Add(_slider);
        _slider.Margin = new Thickness(10, 0, 0, 0);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = left });
        root.Children.Add(_view);
        Content = root;
        Closed += (_, _) => _timer.Stop();
        Opened += (_, _) => Restart();
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private readonly StackPanel _opsPanel = new() { Spacing = 6 };
    private bool _building;

    /// <summary>
    /// One check box per milling or turning operation with a toolpath; the given ones are checked. Milling and turning
    /// are simulated apart (a milled block and a turned part): when the given ones hold both, the milling ones are checked.
    /// </summary>
    private void BuildOperations(IReadOnlyCollection<MachineOp> shown)
    {
        _building = true;
        _ops.Clear();
        _opsPanel.Children.Clear();
        var turning = !shown.Any(MillingSimulation.IsMilling) && shown.Any(o => o is TurningOp);
        foreach (var part in _doc.Parts)
        foreach (var op in part.Operations)
        {
            if (!MillingSimulation.CanSimulate(op) || !_toolpaths.ContainsKey(op)) continue;
            var box = new CheckBox
            {
                Content = _doc.Parts.Count > 1 ? $"{part.Name}: {op.Name}" : op.Name,
                IsChecked = shown.Contains(op) && (op is TurningOp) == turning,
            };
            box.IsCheckedChanged += (_, _) =>
            {
                if (_building) return;
                // Checking a turning operation unchecks the milling ones and the other way round.
                if (box.IsChecked == true)
                {
                    _building = true;
                    foreach (var (other, otherBox) in _ops)
                        if ((other is TurningOp) != (op is TurningOp)) otherBox.IsChecked = false;
                    _building = false;
                }
                Restart();
            };
            _ops.Add((op, box));
            _opsPanel.Children.Add(box);
        }
        if (_ops.Count == 0) _opsPanel.Children.Add(new TextBlock { Text = T("No toolpaths to simulate. Generate toolpaths first."), TextWrapping = TextWrapping.Wrap });
        _building = false;
    }

    /// <summary>
    /// Simulates only <paramref name="ops"/> (the operation or part selected in the drawing tree); the list also
    /// picks up toolpaths generated since the window opened.
    /// </summary>
    public void ShowOperations(IReadOnlyCollection<MachineOp> ops)
    {
        var turning = !ops.Any(MillingSimulation.IsMilling) && ops.Any(o => o is TurningOp);
        var same = _ops.Count == _toolpaths.Count(t => MillingSimulation.CanSimulate(t.Key)) &&
                   _ops.All(o => (o.Box.IsChecked == true) == (ops.Contains(o.Op) && (o.Op is TurningOp) == turning));
        if (same) return;
        _timer.Stop();
        _play.Content = "▷";
        BuildOperations(ops);
        if (IsVisible) Restart();
    }

    /// <summary>Shows the window at most about 85 % of the owner's screen, so that it fits and its edges can be reached.</summary>
    public void ShowFor(Window owner)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            Width = Math.Min(Width, area.Width / screen.Scaling * 0.85);
            Height = Math.Min(Height, area.Height / screen.Scaling * 0.85);
        }
        Show(owner);
    }

    private static Button StepButton(string text, string tip, Action action)
    {
        var b = new Button { Content = text, MinWidth = 36, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return; // typing a cell size or tolerance
        switch (e.Key)
        {
            case Key.Left: RunTo(_done - 1); break;
            case Key.Right: RunTo(_done + 1); break;
            case Key.Home: RunTo(0); break;
            case Key.End: RunTo(Count); break;
            case Key.Space: TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }

    private static Control Row(string label, Control editor)
    {
        var row = new DockPanel();
        DockPanel.SetDock(editor, Dock.Right);
        row.Children.Add(editor);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Control Legend(bool turning)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 8, 0, 0) };
        void Add(Color c, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
            row.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
        if (turning)
        {
            Add(Color.FromRgb(205, 200, 190), T("Material"));
            Add(Color.FromRgb(0, 220, 255), T("Tool"));
            return panel;
        }
        Add(Color.FromRgb(205, 200, 190), T("Material / reached the target"));
        Add(Color.FromRgb(255, 220, 0), T("Rest material (little)"));
        Add(Color.FromRgb(255, 0, 0), T("Rest material (much)"));
        Add(Color.FromRgb(220, 0, 220), T("Cut below the target"));
        Add(Color.FromRgb(0, 220, 255), T("Tool"));
        return panel;
    }

    /// <summary>Cell size, tolerance, export and the colours of rest material belong to milling only.</summary>
    private void ShowMode()
    {
        _cellRow.IsVisible = _toleranceRow.IsVisible = _exports.IsVisible = _millLegend.IsVisible = !_turning;
        _turnLegend.IsVisible = _turning;
    }

    private static double ParseOr(TextBox box, double fallback) =>
        double.TryParse(box.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    /// <summary>New material and moves for the checked operations, simulated to the end.</summary>
    private void Restart()
    {
        var jobs = _ops.Where(o => o.Box.IsChecked == true && _toolpaths.ContainsKey(o.Op)).Select(o => (o.Op, _toolpaths[o.Op])).ToList();
        _generation++;
        _done = 0;
        _checkpoints.Clear();
        _turning = jobs.Count > 0 && jobs.All(j => j.Op is TurningOp);
        ShowMode();
        if (_turning)
        {
            RestartTurning(jobs);
            return;
        }
        _frame = null;
        _turnMoves = [];
        var requested = ParseOr(_cell, 0);
        var field = MillingSimulation.Create(_doc, jobs, requested);
        _cellNote = requested > 0 && field.Cell > requested * 1.0001
            ? F("Cell size enlarged from {0} to {1}: at most {2} cells along a side.", requested.ToString("0.###", CultureInfo.CurrentCulture),
                field.Cell.ToString("0.####", CultureInfo.CurrentCulture), MillingSimulation.MaxCells) + "\n"
            : "";
        _cell.Text = field.Cell.ToString(field.Cell < 0.01 ? "0.####" : "0.###", CultureInfo.CurrentCulture);
        var tolerance = ParseOr(_tolerance, Math.Max(0.02, field.Cell / 2));
        _tolerance.Text = tolerance.ToString("0.###", CultureInfo.CurrentCulture);
        _view.Tolerance = tolerance;
        MillingSimulation.SetTargets(field, _doc, jobs.Select(j => j.Op));
        _moves = MillingSimulation.Moves(_doc, jobs, field.Cell / 2);
        _field = field;
        // About 40 copies, at most ~200 MB in all.
        var maxCopies = (int)Math.Clamp(200_000_000L / (4L * field.Z.Length), 0, 40);
        _checkpointEvery = maxCopies == 0 ? int.MaxValue : Math.Max(20, _moves.Count / maxCopies);
        _view.Field = field;
        StartAtEnd();
    }

    private void RestartTurning(List<(MachineOp Op, Toolpath Toolpath)> jobs)
    {
        _field = null;
        _moves = [];
        _frame = TurningSimulation.FrameOf(_doc, jobs.Select(j => ((TurningOp)j.Op, j.Toolpath)).ToList());
        if (_frame is not { } frame)
        {
            _turnMoves = [];
            _section = [];
            _view.Turned = null;
            _view.InvalidateVisual();
            ShowPosition();
            _info.Text = T("Turning needs a stock: set it in the machining options.");
            return;
        }
        _turnMoves = TurningSimulation.Moves(_doc, frame, jobs);
        _section = [frame.Section];
        // The section is small: a copy every 25 moves.
        _checkpointEvery = 25;
        ShowTurned();
        StartAtEnd();
    }

    private void StartAtEnd()
    {
        _updatingSlider = true;
        _slider.Maximum = Math.Max(1, Count);
        _slider.Value = Count;
        _updatingSlider = false;
        RunTo(Count);
    }

    // ---- the material, for milling or turning

    private void ResetMaterial()
    {
        if (_turning) _section = _frame is { } f ? [f.Section] : [];
        else _field?.Reset();
    }

    private void Restore(object copy)
    {
        if (_turning) _section = (List<Path2>)copy;
        else Array.Copy((float[])copy, _field!.Z, _field.Z.Length);
    }

    /// <summary>
    /// Simulates up to move <paramref name="target"/> on a worker thread. Going back restarts from the nearest
    /// saved copy of the material before it.
    /// </summary>
    private async void RunTo(int target)
    {
        if (_turning ? _frame == null : _field == null) return;
        target = Math.Clamp(target, 0, Count);
        if (_busy)
        {
            _pending = true;
            _updatingSlider = true;
            _slider.Value = target;
            _updatingSlider = false;
            return;
        }
        _busy = true;
        var generation = _generation;
        var sw = Stopwatch.StartNew();
        if (target < _done)
        {
            var restore = _checkpoints.Keys.LastOrDefault(k => k <= target);
            if (restore > 0) Restore(_checkpoints[restore]);
            else ResetMaterial();
            _done = restore;
        }
        // In chunks of about a quarter second, showing the material in between.
        // The worker keeps to these, so that a restart meanwhile does not mix old and new moves.
        var turning = _turning;
        var field = _field;
        var moves = _moves;
        var turnMoves = _turnMoves;
        var k = _done;
        while (k < target)
        {
            var chunkStart = k;
            var every = _checkpointEvery;
            var copies = new List<(int, object)>();
            var section = _section;
            k = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var i = chunkStart;
                while (i < target && watch.ElapsedMilliseconds < 250)
                {
                    if (turning) section = TurningSimulation.Cut(section, turnMoves[i]);
                    else MillingSimulation.Cut(field!, moves[i]);
                    i++;
                    if (i % every == 0) copies.Add((i, turning ? section : field!.Z.Clone()));
                }
                return i;
            });
            if (generation != _generation)
            {
                // Restarted meanwhile: simulate the new material.
                _busy = false;
                _pending = false;
                RunTo((int)Math.Round(_slider.Value));
                return;
            }
            if (turning) _section = section;
            foreach (var (at, copy) in copies) _checkpoints[at] = copy;
            _done = k;
            ShowPosition();
            if (_pending) break; // the slider moved on: follow it
        }
        _busy = false;
        ShowPosition();
        if (!_pending)
        {
            _updatingSlider = true;
            _slider.Value = _done;
            _updatingSlider = false;
        }
        UpdateInfo(sw.Elapsed);
        if (_pending)
        {
            _pending = false;
            RunTo((int)Math.Round(_slider.Value));
        }
    }

    /// <summary>The section as it is now, turned about the axis.</summary>
    private void ShowTurned()
    {
        if (_frame is not { } frame) return;
        var r = frame.Radius;
        var box = frame.Section.Vertices.Select(v => v.XY).ToList();
        var min = new Point3(frame.Origin.X + box.Min(p => p.X), frame.Origin.Y - r, -r);
        var max = new Point3(frame.Origin.X + box.Max(p => p.X), frame.Origin.Y + r, r);
        _view.Turned = new SimulationView.TurnedModel(TurningSimulation.Revolve(frame, _section), min, max);
    }

    /// <summary>Move counter, the last move done and the tool at its end.</summary>
    private void ShowPosition()
    {
        _progress.Text = F("Move {0} of {1}", _done, Count);
        if (_turning) ShowTurned();
        if (_done == 0 || _done > Count)
        {
            _current.Text = T("Start: nothing cut yet.");
            _view.Tool = null;
            _view.TurnedTool = null;
        }
        else if (_turning)
        {
            var m = _turnMoves[_done - 1];
            _current.Text = $"{m.Op.Name} · {Describe(m.Move)}";
            _view.TurnedTool = TurningSimulation.ToolLines(_doc, m, _frame!);
        }
        else
        {
            var m = _moves[_done - 1];
            _current.Text = $"{m.Op.Name} · {Describe(m.Move)}";
            _view.Tool = (m.End, MillingSimulation.ToolOf(_doc, m.Op));
        }
        _view.InvalidateVisual();
    }

    private static string Describe(Move move)
    {
        var p = move.To;
        var xyz = string.Format(CultureInfo.InvariantCulture, "X{0:0.###} Y{1:0.###} Z{2:0.###}", p.X, p.Y, p.Z);
        var feed = move.Feed > 0 ? string.Format(CultureInfo.InvariantCulture, " F{0:0.#}", move.Feed) : "";
        return move.Kind switch
        {
            MoveKind.Rapid => $"G0 {xyz}",
            MoveKind.Feed => $"G1 {xyz}{feed}",
            MoveKind.ArcCW => $"G2 {xyz}{feed}",
            MoveKind.ArcCCW => $"G3 {xyz}{feed}",
            MoveKind.DrillCycle => T("Drilling cycle") + " " + xyz,
            MoveKind.ThreadCycle => T("Threading cycle") + " " + xyz,
            _ => xyz,
        };
    }

    private void UpdateInfo(TimeSpan elapsed)
    {
        if (_turning)
        {
            if (_frame != null) _info.Text = F("Turned part, {0:0.##} s.", elapsed.TotalSeconds);
            return;
        }
        if (_field is not { } f) return;
        var rest = 0;
        var gouge = 0;
        var maxRest = 0.0;
        for (var k = 0; k < f.Z.Length; k++)
        {
            if (float.IsNaN(f.Target[k])) continue;
            var e = f.Z[k] - f.Target[k];
            if (e > _view.Tolerance) { rest++; maxRest = Math.Max(maxRest, e); }
            else if (e < -_view.Tolerance) gouge++;
        }
        var area = f.Cell * f.Cell;
        var unit = _doc.Units == DrawingUnits.Inches ? "in²" : "mm²";
        _info.Text = _cellNote + F("{0} x {1} cells, {2:0.##} s.", f.Nx, f.Ny, elapsed.TotalSeconds) + "\n" +
                     (rest == 0 ? T("No rest material.") : F("Rest material on {0:0.#} {1}, up to {2:0.###} above the target.", rest * area, unit, maxRest)) +
                     (gouge == 0 ? "" : "\n" + F("Cut below the target on {0:0.#} {1}.", gouge * area, unit));
    }

    /// <summary>The material at the move shown, as a mesh; null (with a note) while the simulation is still running.</summary>
    private SurfaceEntity? CurrentSurface()
    {
        if (_turning || _field is not { } field) return null;
        if (_busy)
        {
            _info.Text = T("Please wait until the simulation has stopped.");
            return null;
        }
        return MillingSimulation.ToSurface(field);
    }

    private void AddToDrawing()
    {
        if (_addSurface == null || CurrentSurface() is not { } surface) return;
        _addSurface(surface);
        _info.Text = F("Surface with {0} triangles added to the drawing.", surface.Faces.Count / 3);
    }

    private async void SaveStl()
    {
        if (CurrentSurface() is not { } surface) return;
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = T("Save STL..."),
            SuggestedFileName = (string.IsNullOrEmpty(_doc.Name) ? "simulation" : _doc.Name) + "-simulation.stl",
            DefaultExtension = "stl",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(T("STL mesh")) { Patterns = ["*.stl"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            BamCam.IO.Stl.StlWriter.Save(surface, path);
            _info.Text = F("Saved {0} ({1} triangles).", path, surface.Faces.Count / 3);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _info.Text = F("Could not save {0}: {1}", path, ex.Message);
        }
    }

    /// <summary>Diagnostic: jumps back and forth and steps single moves, as a user would (--window simulation).</summary>
    public async Task ExerciseAsync()
    {
        var random = new Random(1);
        for (var i = 0; i < 40; i++)
        {
            var target = i % 4 == 0 ? random.Next(Count + 1) : _done + (i % 2 == 0 ? 1 : -1);
            RunTo(target);
            await Task.Delay(60);
        }
        RunTo(Count / 2);
        await Task.Delay(1500);
    }

    private void TogglePlay()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _play.Content = "▷";
            return;
        }
        if (_done >= Count) RunTo(0);
        _due = 1; // the first move at once
        _tickWatch.Restart();
        _timer.Start();
        _play.Content = "❚❚";
    }
}
