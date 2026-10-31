using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Tools ▸ Speeds and Feeds: spindle speed and feeds from material, tool diameter and flutes; Apply writes them
/// into the machining operation the window was opened for.
/// </summary>
public sealed class SpeedsFeedsWindow : Window
{
    private readonly List<CuttingMaterial> _materials;
    private readonly SpeedsFeedsSettings _settings;
    private readonly DrawingUnits _units;
    private readonly ComboBox _material = new() { MinWidth = 200 };
    private readonly NumericUpDown _diameter = Number(0.001m, 3);
    private readonly NumericUpDown _flutes = Number(1, 0);
    private readonly NumericUpDown _speed = Number(1, 0);
    private readonly NumericUpDown _chipLoad = Number(0.0001m, 4);
    private readonly NumericUpDown _minRpm = Number(100, 0);
    private readonly NumericUpDown _maxRpm = Number(100, 0);
    private readonly NumericUpDown _plunge = Number(0.05m, 2);
    private readonly TextBlock _result = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _note = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button _apply = new() { Content = T("Apply"), IsDefault = true, MinWidth = 90 };
    private bool _updating;
    private bool _chipLoadFromMaterial = true;

    /// <summary>The values to write when the user pressed Apply, else null.</summary>
    public CuttingResult? Applied { get; private set; }

    public SpeedsFeedsWindow(List<CuttingMaterial> materials, SpeedsFeedsSettings settings, DrawingUnits units,
        double diameter, int flutes, string? target)
    {
        _materials = materials;
        _settings = settings;
        _units = units;
        Title = T("Speeds and Feeds");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var unit = units == DrawingUnits.Inches ? "in" : units == DrawingUnits.Millimeters ? "mm" : T("units");
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(12),
            ColumnSpacing = 8,
            RowSpacing = 6,
        };
        var row = 0;
        void Row(string label, Control input, string? suffix = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, row);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);
            grid.Children.Add(l);
            grid.Children.Add(input);
            if (suffix != null)
            {
                var s = new TextBlock { Text = suffix, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
                Grid.SetRow(s, row);
                Grid.SetColumn(s, 2);
                grid.Children.Add(s);
            }
            row++;
        }
        Row(T("Material"), _material);
        Row(T("Tool Diameter"), _diameter, unit);
        Row(T("Flutes"), _flutes);
        Row(T("Cutting Speed"), _speed, "m/min");
        Row(T("Chip Load"), _chipLoad, F("{0}/tooth", unit));
        Row(T("Minimum Spindle Speed"), _minRpm, T("rpm"));
        Row(T("Maximum Spindle Speed"), _maxRpm, T("rpm"));
        Row(T("Plunge Factor"), _plunge, T("× feed"));

        _material.ItemsSource = materials.Select(m => T(m.Name)).ToList();
        _material.HorizontalAlignment = HorizontalAlignment.Stretch;
        _diameter.Value = (decimal)Math.Max(diameter, 0.001);
        _flutes.Value = Math.Max(flutes, 1);
        _flutes.Maximum = 20;
        _minRpm.Value = (decimal)Math.Max(settings.MinRpm, 0);
        _maxRpm.Value = (decimal)Math.Max(settings.MaxRpm, 0);
        _plunge.Value = (decimal)Math.Clamp(settings.PlungeFactor, 0, 1);
        _plunge.Maximum = 1;
        var index = materials.FindIndex(m => m.Name == settings.Material);
        _material.SelectedIndex = Math.Max(index, 0);
        FromMaterial();

        _material.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _chipLoadFromMaterial = true;
            FromMaterial();
        };
        _diameter.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            if (_chipLoadFromMaterial) FromMaterial();
            else Update();
        };
        _chipLoad.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _chipLoadFromMaterial = false;
            Update();
        };
        foreach (var n in new[] { _flutes, _speed, _minRpm, _maxRpm, _plunge })
            n.ValueChanged += (_, _) =>
            {
                if (!_updating) Update();
            };

        var info = new TextBlock
        {
            Text = target != null ? F("Apply sets spindle speed, cut and plunge feedrate of \"{0}\".", target)
                : T("Select a machining operation to apply the values to it."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
        };
        _apply.IsEnabled = target != null;
        _apply.Click += (_, _) =>
        {
            Applied = Calculate();
            Close();
        };
        var close = new Button { Content = T("Close"), IsCancel = true, MinWidth = 90 };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_apply);
        buttons.Children.Add(close);

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(grid);
        var results = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0) };
        results.Children.Add(new Separator());
        results.Children.Add(_result);
        results.Children.Add(_note);
        results.Children.Add(info);
        buttons.Margin = new Thickness(12, 4, 12, 0);
        panel.Children.Add(results);
        panel.Children.Add(buttons);
        Content = panel;
        Closing += (_, _) => Remember();
        Update();
    }

    private static NumericUpDown Number(decimal increment, int decimals) => new()
    {
        Minimum = 0,
        Maximum = 1_000_000,
        Increment = increment,
        FormatString = decimals == 0 ? "0" : "0." + new string('#', decimals),
        NumberFormat = BamCam.Core.Localization.Tr.Culture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static double D(NumericUpDown n) => (double)(n.Value ?? 0);

    private CuttingMaterial Material => _materials[Math.Clamp(_material.SelectedIndex, 0, _materials.Count - 1)];

    /// <summary>Cutting speed and chip load from the material table (fz scales with the diameter).</summary>
    private void FromMaterial()
    {
        _updating = true;
        _speed.Value = (decimal)Material.CuttingSpeed;
        _chipLoad.Value = (decimal)Math.Round(Material.ChipLoadFactor * D(_diameter), 4);
        _updating = false;
        Update();
    }

    private CuttingResult? Calculate()
    {
        if (D(_diameter) <= 0) return null;
        return CuttingData.Calculate(D(_diameter), (int)D(_flutes), D(_speed), D(_chipLoad), _units, D(_minRpm), D(_maxRpm), D(_plunge));
    }

    private void Update()
    {
        if (Calculate() is not { } r)
        {
            _result.Text = T("Enter a tool diameter.");
            _note.Text = "";
            _apply.IsEnabled = false;
            return;
        }
        var unit = _units == DrawingUnits.Inches ? "in/min" : _units == DrawingUnits.Millimeters ? "mm/min" : T("units/min");
        _result.Text = F("Spindle speed: {0:0} rpm\nCut feedrate: {1:0.#} {3}\nPlunge feedrate: {2:0.#} {3}", r.SpindleSpeed, r.CutFeedrate, r.PlungeFeedrate, unit);
        _note.Text = Math.Abs(r.CuttingSpeed - D(_speed)) > 0.5
            ? F("Limited by the spindle speed range: effective cutting speed {0:0} m/min.", r.CuttingSpeed)
            : T("Starting values for carbide end mills: check them against the tool maker's data.");
    }

    private void Remember()
    {
        _settings.Material = Material.Name;
        _settings.MinRpm = D(_minRpm);
        _settings.MaxRpm = D(_maxRpm);
        _settings.PlungeFactor = D(_plunge);
    }
}
