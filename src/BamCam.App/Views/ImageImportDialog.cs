using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BamCam.App.Controls;
using BamCam.App.Services;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Settings of an image import with a live preview: the traced outlines over the image, or the grey values as they
/// become heights. The preview works on a reduced copy of the image so that it follows every change at once.
/// </summary>
public sealed class ImageImportDialog : Window
{
    private const int PreviewSize = 480;
    private readonly float[,] _preview;
    private readonly double _reduction; // preview pixels per image pixel
    private readonly object _settings;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _overlay = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4), FontSize = 11 };
    private bool _ok;

    public ImageImportDialog(string title, object settings, float[,] gray, string hint)
    {
        _settings = settings;
        Title = title;
        Width = 900;
        Height = 560;
        MinWidth = 600;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        (_preview, _reduction) = Reduce(gray, PreviewSize);

        var grid = new PropertyGrid { NameColumnWidth = 150, SelectedObject = settings, Width = 340 };
        grid.ValueChanged += (_, _) => UpdatePreview();

        var ok = new Button { Content = T("OK"), IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            _ok = true;
            Close();
        };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        // Image and overlay share one Viewbox, so the outlines stay on the image at any window size.
        var picture = new Grid { Width = _preview.GetLength(0), Height = _preview.GetLength(1) };
        picture.Children.Add(_image);
        picture.Children.Add(_overlay);
        var view = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Margin = new Thickness(8),
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = picture },
        };

        var left = new DockPanel();
        var hintText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6), FontSize = 11 };
        DockPanel.SetDock(hintText, Dock.Top);
        left.Children.Add(hintText);
        left.Children.Add(grid);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(_info, Dock.Bottom);
        root.Children.Add(_info);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(view);
        Content = root;
        UpdatePreview();
    }

    public async Task<bool> ShowFor(Window owner)
    {
        await ShowDialog(owner);
        return _ok;
    }

    /// <summary>Image scaled down to at most <paramref name="max"/> pixels per side (box average) and the factor.</summary>
    private static (float[,] Gray, double Factor) Reduce(float[,] gray, int max)
    {
        int w = gray.GetLength(0), h = gray.GetLength(1);
        var f = Math.Min(1.0, (double)max / Math.Max(w, h));
        if (f >= 1) return (gray, 1);
        int pw = Math.Max(2, (int)(w * f)), ph = Math.Max(2, (int)(h * f));
        var result = new float[pw, ph];
        for (var x = 0; x < pw; x++)
        for (var y = 0; y < ph; y++)
        {
            int x0 = x * w / pw, x1 = Math.Max(x0 + 1, (x + 1) * w / pw), y0 = y * h / ph, y1 = Math.Max(y0 + 1, (y + 1) * h / ph);
            double sum = 0;
            for (var i = x0; i < x1; i++)
            for (var j = y0; j < y1; j++) sum += gray[i, j];
            result[x, y] = (float)(sum / ((x1 - x0) * (y1 - y0)));
        }
        return (result, f);
    }

    private void UpdatePreview()
    {
        int w = _preview.GetLength(0), h = _preview.GetLength(1);
        var invertHeights = _settings is HeightMapOptions { Invert: true };
        _image.Source = ToBitmap(_preview, invertHeights);
        _overlay.Children.Clear();
        switch (_settings)
        {
            case TraceOptions t:
            {
                // Outlines in preview pixels (width = pixel count); smoothing scaled to the reduced image.
                var loops = ImageGeometry.TraceOutlines(_preview, w, Math.Clamp(t.Threshold, 0, 1), t.Invert, t.Smoothing * _reduction);
                foreach (var loop in loops)
                {
                    var pts = loop.Flatten(0.5).Select(p => new Point(p.X, h - p.Y)).ToList();
                    if (pts.Count < 2) continue;
                    _overlay.Children.Add(new Avalonia.Controls.Shapes.Polygon { Points = pts, Stroke = Brushes.Red, StrokeThickness = Math.Max(1, w / 300.0) });
                }
                var height = t.Width * h / w;
                _info.Text = F("{0} outlines, {1:0.##} x {2:0.##} in the drawing.", loops.Count, t.Width, height);
                break;
            }
            case HeightMapOptions m:
            {
                var height = m.Width * h / w;
                _info.Text = F("White = Top Z {0:0.###}, black = {1:0.###}. Surface {2:0.##} x {3:0.##}.", m.TopZ, m.TopZ - m.Depth, m.Width, height);
                break;
            }
        }
    }

    /// <summary>Grey values as a bitmap (row 0 at the top), inverted when black becomes high.</summary>
    private static WriteableBitmap ToBitmap(float[,] gray, bool invert)
    {
        int w = gray.GetLength(0), h = gray.GetLength(1);
        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var row = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var g = gray[x, y];
                var v = (byte)Math.Clamp((invert ? 1 - g : g) * 255, 0, 255);
                row[4 * x] = row[4 * x + 1] = row[4 * x + 2] = v;
                row[4 * x + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}
