using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>Shown while toolpaths take longer than a moment: what is running, for how long, and a way out.</summary>
public sealed class GenerateProgressWindow : Window
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { Foreground = Brushes.Gray };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _start = DateTime.Now;

    public GenerateProgressWindow(Action cancel)
    {
        Title = T("Generating toolpaths");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var button = new Button { Content = T("Cancel"), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, MinWidth = 80 };
        button.Click += (_, _) =>
        {
            button.IsEnabled = false;
            cancel();
        };
        Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { _text, _time, button } };

        void Tick() => _time.Text = F("Running for {0:mm\\:ss}", DateTime.Now - _start);
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
        // Closing the window is the same as the button.
        Closing += (_, _) =>
        {
            if (!_finished) cancel();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private bool _finished;

    /// <summary>The work is over: closes the window without cancelling anything.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    public string Message
    {
        set => _text.Text = value;
    }
}
