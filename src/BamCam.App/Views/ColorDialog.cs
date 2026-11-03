using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Picks a colour by clicking: a row of common drawing colours, and a colour field with sliders, palette and RGB / hex
/// input (Avalonia's colour view). Double click on a quick colour takes it at once.
/// </summary>
public sealed class ColorDialog : Window
{
    /// <summary>Colours used most for layers (CAD style): basic colours, greys and a few in between.</summary>
    private static readonly RgbColor[] QuickColors =
    [
        new(255, 0, 0), new(255, 127, 0), new(255, 255, 0), new(127, 255, 0), new(0, 255, 0), new(0, 255, 127),
        new(0, 255, 255), new(0, 127, 255), new(0, 0, 255), new(127, 0, 255), new(255, 0, 255), new(255, 0, 127),
        new(255, 255, 255), new(192, 192, 192), new(128, 128, 128), new(64, 64, 64), new(0, 0, 0), new(255, 165, 0),
        new(128, 0, 0), new(128, 128, 0), new(0, 128, 0), new(0, 128, 128), new(0, 0, 128), new(128, 0, 128),
    ];

    private readonly ColorView _view;
    private RgbColor? _result;

    private ColorDialog(RgbColor initial)
    {
        Title = T("Choose colour");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _view = new ColorView
        {
            Color = Color.FromRgb(initial.R, initial.G, initial.B),
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            IsAccentColorsVisible = false,
            Width = 340,
            Height = 380,
        };

        var quick = new Avalonia.Controls.Primitives.UniformGrid { Columns = 12, Width = 340, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in QuickColors)
        {
            var color = Color.FromRgb(c.R, c.G, c.B);
            var swatch = new Button
            {
                Height = 22, Margin = new Thickness(1), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(swatch, c.ToString());
            swatch.Click += (_, _) => _view.Color = color;
            swatch.DoubleTapped += (_, _) => Accept();
            quick.Children.Add(swatch);
        }

        var ok = new Button { Content = T("OK"), IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(quick);
        root.Children.Add(_view);
        root.Children.Add(buttons);
        Content = root;
    }

    private void Accept()
    {
        var c = _view.Color;
        _result = new RgbColor(c.R, c.G, c.B);
        Close();
    }

    /// <summary>The window alone, for the --window color screenshot.</summary>
    internal static ColorDialog Preview(RgbColor initial) => new(initial);

    /// <summary>The chosen colour, or null when cancelled.</summary>
    public static async Task<RgbColor?> Pick(Window owner, RgbColor initial)
    {
        var dialog = new ColorDialog(initial);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
