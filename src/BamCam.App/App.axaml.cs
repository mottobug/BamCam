using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BamCam.App.Views;

namespace BamCam.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var args = desktop.Args ?? [];
            var shot = Array.IndexOf(args, "--screenshot");
            string? screenshotPath = null;
            if (shot >= 0 && shot + 1 < args.Length)
            {
                screenshotPath = args[shot + 1];
                args = args.Where((_, i) => i != shot && i != shot + 1).ToArray();
            }
            var generate = args.Contains("--generate");
            // Diagnostic options with a value: --inspect <part/MOP name>, --filter <index>[,<z level>],
            // --drag-tab <fromX,fromY,toX,toY> (drawing coordinates).
            var options = new Dictionary<string, string>();
            foreach (var name in new[] { "--inspect", "--filter", "--drag-tab", "--config" })
            {
                var i = Array.IndexOf(args, name);
                if (i < 0 || i + 1 >= args.Length) continue;
                options[name] = args[i + 1];
                args = args.Where((_, k) => k != i && k != i + 1).ToArray();
            }
            var files = args.Where(a => !a.StartsWith("--")).ToArray();
            if (files.Length > 0) window.OpenFileOnStartup(files[0]);
            if (screenshotPath != null) window.CaptureScreenshotAndExit(screenshotPath, generate, options);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
