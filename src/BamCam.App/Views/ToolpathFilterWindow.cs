using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.App.Drawing;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Toolpath View Filter (Ctrl+F, modeless): steps through toolpaths in execution order and/or by Z level.
/// </summary>
public sealed class ToolpathFilterWindow : Window
{
    private readonly DrawingView _view;
    private readonly ToolpathFilterSettings _f;
    private readonly CheckBox _indexCheck = new() { Content = T("Toolpath index") };
    private readonly NumericUpDown _index = new() { Minimum = 1, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock _indexInfo = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
    private readonly CheckBox _zCheck = new() { Content = T("Z depth index") };
    private readonly NumericUpDown _z = new() { Minimum = 1, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock _zInfo = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
    private readonly CheckBox _cutCheck = new() { Content = T("Cut toolpath color") };
    private readonly CheckBox _highlightCheck = new() { Content = T("Toolpath color") };
    private readonly Slider _width = new() { Minimum = 0.5, Maximum = 6, Width = 150 };
    private readonly Slider _alpha = new() { Minimum = 0.1, Maximum = 1, Width = 150 };
    private bool _updating;

    public ToolpathFilterWindow(DrawingView view)
    {
        _view = view;
        _f = view.Settings.Filter;
        Title = T("Toolpath View Filter");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(12),
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        void Add(Control c, int row, int col)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            c.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(c);
        }
        Add(_indexCheck, 0, 0); Add(_index, 0, 1); Add(_indexInfo, 0, 2);
        Add(_zCheck, 1, 0); Add(_z, 1, 1); Add(_zInfo, 1, 2);
        Add(_cutCheck, 2, 0); Add(Swatch(_f.CutColor), 2, 1);
        Add(_highlightCheck, 3, 0); Add(Swatch(_f.HighlightColor), 3, 1);
        Add(new TextBlock { Text = T("Line width") }, 4, 0); Add(_width, 4, 1);
        Add(new TextBlock { Text = T("Alpha") }, 5, 0); Add(_alpha, 5, 1);
        Content = grid;

        _indexCheck.IsCheckedChanged += (_, _) => Apply();
        _zCheck.IsCheckedChanged += (_, _) => Apply();
        _cutCheck.IsCheckedChanged += (_, _) => Apply();
        _highlightCheck.IsCheckedChanged += (_, _) => Apply();
        _index.ValueChanged += (_, _) => Apply();
        _z.ValueChanged += (_, _) => Apply();
        _width.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Apply(); };
        _alpha.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Apply(); };
        if (view.Session is { } s) s.ToolpathsChanged += (_, _) => Load();
        Load();
    }

    private static Border Swatch(Color c) => new()
    {
        Width = 40, Height = 16, Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>Copies the settings into the controls and refreshes the ranges.</summary>
    public void Load()
    {
        _updating = true;
        _indexCheck.IsChecked = _f.IndexEnabled;
        _zCheck.IsChecked = _f.ZEnabled;
        _cutCheck.IsChecked = _f.ShowCut;
        _highlightCheck.IsChecked = _f.Highlight;
        _width.Value = _f.LineWidth;
        _alpha.Value = _f.Alpha;
        UpdateInfo();
        _f.Index = Math.Clamp(_f.Index, 1, (int)_index.Maximum);
        _f.ZIndex = Math.Clamp(_f.ZIndex, 1, (int)_z.Maximum);
        _index.Value = _f.Index;
        _z.Value = _f.ZIndex;
        _updating = false;
        UpdateInfo();
    }

    private void Apply()
    {
        if (_updating) return;
        _f.IndexEnabled = _indexCheck.IsChecked == true;
        _f.ZEnabled = _zCheck.IsChecked == true;
        _f.ShowCut = _cutCheck.IsChecked == true;
        _f.Highlight = _highlightCheck.IsChecked == true;
        _f.Index = (int)(_index.Value ?? 1);
        _f.ZIndex = (int)(_z.Value ?? 1);
        _f.LineWidth = _width.Value;
        _f.Alpha = _alpha.Value;
        UpdateInfo();
        _view.Invalidate();
    }

    private void UpdateInfo()
    {
        var (paths, levels) = _view.FilterPaths();
        _index.Maximum = Math.Max(1, paths.Count);
        _z.Maximum = Math.Max(1, levels.Count);
        _indexInfo.Text = F("of {0}", paths.Count);
        var zi = Math.Clamp(_f.ZIndex, 1, Math.Max(1, levels.Count));
        _zInfo.Text = levels.Count == 0 ? F("of {0}", 0)
            : F("of {0}  (Z {1})", levels.Count, levels[zi - 1].ToString("0.###", CultureInfo.InvariantCulture));
    }
}
