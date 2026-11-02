using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// depth of each operation shown in colour. The slider steps through the moves.
/// </summary>
public sealed class SimulationWindow : Window
{
    private readonly CadDocument _doc;
    private readonly IReadOnlyDictionary<MachineOp, Toolpath> _toolpaths;
    private readonly List<(MachineOp Op, CheckBox Box)> _ops = [];
    private readonly SimulationView _view = new();
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _progress = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 150, Margin = new Thickness(8, 0) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _cell = new() { Width = 80 };
    private readonly TextBox _tolerance = new() { Width = 80 };
    private readonly Button _play = new() { Content = "▶", MinWidth = 36 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private HeightField? _field;
    private List<MillingSimulation.Step> _steps = [];
    private int _done; // steps applied to _field
    private bool _busy, _pending, _updatingSlider;

    public SimulationWindow(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths)
    {
        _doc = doc;
        _toolpaths = toolpaths;
        Title = T("3D Simulation");
        Width = 1100;
        Height = 720;
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
            Text = T("Left drag: rotate, right drag: move, wheel: zoom, double click: reset."), TextWrapping = TextWrapping.Wrap,
            FontSize = 11, Foreground = Brushes.DimGray,
        });

        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingSlider) RunTo((int)_slider.Value);
        };
        _play.Click += (_, _) => TogglePlay();
        _timer.Tick += (_, _) =>
        {
            if (_busy) return;
            if (_done >= _steps.Count)
            {
                TogglePlay();
                return;
            }
            RunTo(_done + Math.Max(1, _steps.Count / 300));
        };
        var bottom = new DockPanel { Margin = new Thickness(10, 4, 10, 10) };
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(_progress, Dock.Right);
        bottom.Children.Add(_play);
        bottom.Children.Add(_progress);
        bottom.Children.Add(_slider);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = left });
        root.Children.Add(_view);
        Content = root;
        Closed += (_, _) => _timer.Stop();
        Opened += (_, _) => Restart();
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
        return panel;
    }

    private static double ParseOr(TextBox box, double fallback) =>
        double.TryParse(box.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    /// <summary>New height field and steps for the checked operations, simulated to the end.</summary>
    private void Restart()
    {
        var jobs = _ops.Where(o => o.Box.IsChecked == true && _toolpaths.ContainsKey(o.Op)).Select(o => (o.Op, _toolpaths[o.Op])).ToList();
        var field = MillingSimulation.Create(_doc, jobs, ParseOr(_cell, 0));
        _cell.Text = field.Cell.ToString("0.###", CultureInfo.CurrentCulture);
        var tolerance = ParseOr(_tolerance, Math.Max(0.02, field.Cell / 2));
        _tolerance.Text = tolerance.ToString("0.###", CultureInfo.CurrentCulture);
        _view.Tolerance = tolerance;
        MillingSimulation.SetTargets(field, _doc, jobs.Select(j => j.Op));
        _steps = MillingSimulation.Steps(_doc, jobs, field.Cell / 2);
        _field = field;
        _done = 0;
        _view.Field = field;
        _updatingSlider = true;
        _slider.Maximum = Math.Max(1, _steps.Count);
        _slider.Value = _steps.Count;
        _updatingSlider = false;
        RunTo(_steps.Count);
    }

    /// <summary>Simulates up to step <paramref name="target"/> on a worker thread (from the start when going back).</summary>
    private async void RunTo(int target)
    {
        if (_field is not { } field) return;
        target = Math.Clamp(target, 0, _steps.Count);
        if (_busy)
        {
            _pending = true;
            return;
        }
        _busy = true;
        var sw = Stopwatch.StartNew();
        var from = _done;
        var steps = _steps;
        if (target < from)
        {
            field.Reset();
            from = _done = 0;
        }
        // In chunks of about a quarter second, showing the material in between.
        var k = from;
        while (k < target)
        {
            var chunkStart = k;
            k = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var i = chunkStart;
                while (i < target && watch.ElapsedMilliseconds < 250) MillingSimulation.Cut(field, steps[i++]);
                return i;
            });
            if (field != _field)
            {
                // Restarted meanwhile: simulate the new field.
                _busy = false;
                _pending = false;
                RunTo((int)_slider.Value);
                return;
            }
            _done = k;
            _view.InvalidateVisual();
            _progress.Text = F("Move {0} / {1}", _done, _steps.Count);
            if (_pending) break; // the slider moved on: follow it
        }
        _busy = false;
        _updatingSlider = true;
        _slider.Value = _pending ? _slider.Value : _done;
        _updatingSlider = false;
        UpdateInfo(sw.Elapsed);
        if (_pending)
        {
            _pending = false;
            RunTo((int)_slider.Value);
        }
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
            _play.Content = "▶";
            return;
        }
        if (_done >= _steps.Count) RunTo(0);
        _timer.Start();
        _play.Content = "❚❚";
    }
}
