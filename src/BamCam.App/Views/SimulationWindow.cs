using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BamCam.App.Controls;
using BamCam.Core.Cam;
using BamCam.Core.Simulation;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// 3D simulation of the milling operations on the stock (height field), with the material left above the target
/// depth of each operation shown in colour. The moves can be stepped one by one, forwards and backwards.
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private HeightField? _field;
    private List<MillingSimulation.SimMove> _moves = [];
    private int _done; // moves applied to _field
    private bool _busy, _pending, _updatingSlider;

    /// <summary>Copies of the heights after every n-th move, so that stepping back does not start from the beginning.</summary>
    private readonly SortedDictionary<int, float[]> _checkpoints = [];
    private int _checkpointEvery = int.MaxValue;

    public SimulationWindow(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths)
    {
        _doc = doc;
        _toolpaths = toolpaths;
        Title = T("3D Simulation");
        Width = 1100;
        Height = 740;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var left = new StackPanel { Margin = new Thickness(10), Spacing = 6, Width = 250 };
        left.Children.Add(new TextBlock { Text = T("Operations"), FontWeight = FontWeight.SemiBold });
        foreach (var op in doc.Parts.SelectMany(p => p.Operations))
        {
            if (!MillingSimulation.IsMilling(op) || !toolpaths.ContainsKey(op)) continue;
            var box = new CheckBox { Content = op.Name, IsChecked = op.Enabled };
            box.IsCheckedChanged += (_, _) => Restart();
            _ops.Add((op, box));
            left.Children.Add(box);
        }
        if (_ops.Count == 0) left.Children.Add(new TextBlock { Text = T("No milling toolpaths. Generate toolpaths first."), TextWrapping = TextWrapping.Wrap });

        left.Children.Add(Row(T("Cell size"), _cell));
        left.Children.Add(Row(T("Tolerance"), _tolerance));
        var apply = new Button { Content = T("Simulate"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        apply.Click += (_, _) => Restart();
        left.Children.Add(apply);
        left.Children.Add(Legend());
        left.Children.Add(_info);
        left.Children.Add(new TextBlock
        {
            Text = T("Left drag: rotate, right drag: move, wheel: zoom, double click: reset. Arrow keys: previous / next move, Home / End: start / end, Space: play."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray,
        });

        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingSlider) RunTo((int)Math.Round(_slider.Value));
        };
        _play.Click += (_, _) => TogglePlay();
        ToolTip.SetTip(_play, T("Play / pause (Space)"));
        _timer.Tick += (_, _) =>
        {
            if (_busy) return;
            if (_done >= _moves.Count)
            {
                TogglePlay();
                return;
            }
            RunTo(_done + Math.Max(1, _moves.Count / 300));
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(StepButton("|◀", T("To the start (Home)"), () => RunTo(0)));
        buttons.Children.Add(StepButton("◀", T("Previous move (Left)"), () => RunTo(_done - 1)));
        buttons.Children.Add(_play);
        buttons.Children.Add(StepButton("▶", T("Next move (Right)"), () => RunTo(_done + 1)));
        buttons.Children.Add(StepButton("▶|", T("To the end (End)"), () => RunTo(_moves.Count)));

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
            case Key.End: RunTo(_moves.Count); break;
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

    private static Control Legend()
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 8, 0, 0) };
        void Add(Color c, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
            row.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
        Add(Color.FromRgb(205, 200, 190), T("Material / reached the target"));
        Add(Color.FromRgb(255, 220, 0), T("Rest material (little)"));
        Add(Color.FromRgb(255, 0, 0), T("Rest material (much)"));
        Add(Color.FromRgb(220, 0, 220), T("Cut below the target"));
        Add(Color.FromRgb(0, 220, 255), T("Tool"));
        return panel;
    }

    private static double ParseOr(TextBox box, double fallback) =>
        double.TryParse(box.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    /// <summary>New height field and moves for the checked operations, simulated to the end.</summary>
    private void Restart()
    {
        var jobs = _ops.Where(o => o.Box.IsChecked == true && _toolpaths.ContainsKey(o.Op)).Select(o => (o.Op, _toolpaths[o.Op])).ToList();
        var field = MillingSimulation.Create(_doc, jobs, ParseOr(_cell, 0));
        _cell.Text = field.Cell.ToString("0.###", CultureInfo.CurrentCulture);
        var tolerance = ParseOr(_tolerance, Math.Max(0.02, field.Cell / 2));
        _tolerance.Text = tolerance.ToString("0.###", CultureInfo.CurrentCulture);
        _view.Tolerance = tolerance;
        MillingSimulation.SetTargets(field, _doc, jobs.Select(j => j.Op));
        _moves = MillingSimulation.Moves(_doc, jobs, field.Cell / 2);
        _field = field;
        _done = 0;
        _checkpoints.Clear();
        // About 40 copies, at most ~200 MB in all.
        var maxCopies = (int)Math.Clamp(200_000_000L / (4L * field.Z.Length), 0, 40);
        _checkpointEvery = maxCopies == 0 ? int.MaxValue : Math.Max(20, _moves.Count / maxCopies);
        _view.Field = field;
        _updatingSlider = true;
        _slider.Maximum = Math.Max(1, _moves.Count);
        _slider.Value = _moves.Count;
        _updatingSlider = false;
        RunTo(_moves.Count);
    }

    /// <summary>
    /// Simulates up to move <paramref name="target"/> on a worker thread. Going back restarts from the nearest
    /// saved copy of the heights before it.
    /// </summary>
    private async void RunTo(int target)
    {
        if (_field is not { } field) return;
        target = Math.Clamp(target, 0, _moves.Count);
        if (_busy)
        {
            _pending = true;
            _updatingSlider = true;
            _slider.Value = target;
            _updatingSlider = false;
            return;
        }
        _busy = true;
        var sw = Stopwatch.StartNew();
        var moves = _moves;
        if (target < _done)
        {
            var restore = _checkpoints.Keys.LastOrDefault(k => k <= target);
            if (restore > 0) Array.Copy(_checkpoints[restore], field.Z, field.Z.Length);
            else field.Reset();
            _done = restore;
        }
        // In chunks of about a quarter second, showing the material in between.
        var k = _done;
        while (k < target)
        {
            var chunkStart = k;
            var every = _checkpointEvery;
            var copies = new List<(int, float[])>();
            k = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var i = chunkStart;
                while (i < target && watch.ElapsedMilliseconds < 250)
                {
                    MillingSimulation.Cut(field, moves[i++]);
                    if (i % every == 0) copies.Add((i, (float[])field.Z.Clone()));
                }
                return i;
            });
            if (field != _field)
            {
                // Restarted meanwhile: simulate the new field.
                _busy = false;
                _pending = false;
                RunTo((int)Math.Round(_slider.Value));
                return;
            }
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

    /// <summary>Move counter, the last move done and the tool at its end.</summary>
    private void ShowPosition()
    {
        _progress.Text = F("Move {0} of {1}", _done, _moves.Count);
        if (_done == 0 || _done > _moves.Count)
        {
            _current.Text = T("Start: nothing cut yet.");
            _view.Tool = null;
        }
        else
        {
            var m = _moves[_done - 1];
            _current.Text = $"{m.Op.Name} · {Describe(m)}";
            _view.Tool = (m.End, MillingSimulation.ToolOf(_doc, m.Op).Radius);
        }
        _view.InvalidateVisual();
    }

    private static string Describe(MillingSimulation.SimMove m)
    {
        var p = m.Move.To;
        var xyz = string.Format(CultureInfo.InvariantCulture, "X{0:0.###} Y{1:0.###} Z{2:0.###}", p.X, p.Y, p.Z);
        var feed = m.Move.Feed > 0 ? string.Format(CultureInfo.InvariantCulture, " F{0:0.#}", m.Move.Feed) : "";
        return m.Move.Kind switch
        {
            MoveKind.Rapid => $"G0 {xyz}",
            MoveKind.Feed => $"G1 {xyz}{feed}",
            MoveKind.ArcCW => $"G2 {xyz}{feed}",
            MoveKind.ArcCCW => $"G3 {xyz}{feed}",
            MoveKind.DrillCycle => T("Drilling cycle") + " " + xyz,
            _ => xyz,
        };
    }

    private void UpdateInfo(TimeSpan elapsed)
    {
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
        _info.Text = F("{0} x {1} cells, {2:0.##} s.", f.Nx, f.Ny, elapsed.TotalSeconds) + "\n" +
                     (rest == 0 ? T("No rest material.") : F("Rest material on {0:0.#} {1}, up to {2:0.###} above the target.", rest * area, unit, maxRest)) +
                     (gouge == 0 ? "" : "\n" + F("Cut below the target on {0:0.#} {1}.", gouge * area, unit));
    }

    private void TogglePlay()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _play.Content = "▷";
            return;
        }
        if (_done >= _moves.Count) RunTo(0);
        _timer.Start();
        _play.Content = "❚❚";
    }
}
