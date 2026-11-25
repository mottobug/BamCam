using Avalonia;
using BamCam.App.Services;

namespace BamCam.App;

internal static class Program
{
    /// <summary>On Linux: the rendering modes in use ("software", "glx,software", ...), else null.</summary>
    internal static string? RenderingChoice { get; private set; }

    /// <summary>Where <see cref="RenderingChoice"/> comes from: "--rendering", "BAMCAM_RENDERING" or "settings".</summary>
    internal static string? RenderingSource { get; private set; }

    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    private static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        if (!OperatingSystem.IsLinux()) return builder;

        // Linux: how windows are drawn. With OpenGL (Avalonia's default) some graphics drivers, often without a
        // compositor (e.g. on LinuxCNC machines), make the window flicker on every mouse move and show what is behind
        // it; software drawing with the processor never does. The start option --rendering and BAMCAM_RENDERING
        // (glx, egl, vulkan, software; several with commas, in order) take precedence over Tools > Options.
        var index = Array.IndexOf(args, "--rendering");
        string? choice;
        if (index >= 0 && index + 1 < args.Length)
        {
            choice = args[index + 1];
            RenderingSource = "--rendering";
        }
        else if (Environment.GetEnvironmentVariable("BAMCAM_RENDERING") is { Length: > 0 } variable)
        {
            choice = variable;
            RenderingSource = "BAMCAM_RENDERING";
        }
        else
        {
            var path = Environment.GetEnvironmentVariable("BAMCAM_SETTINGS") is { Length: > 0 } p ? p : AppSettings.DefaultPath;
            choice = AppSettings.Load(path, out _).LinuxRendering == LinuxRendering.Software ? "software" : "glx";
            RenderingSource = "settings";
        }

        var modes = choice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => Enum.TryParse<X11RenderingMode>(m, ignoreCase: true, out var mode) ? mode : (X11RenderingMode?)null)
            .OfType<X11RenderingMode>().Distinct().ToList();
        if (modes.Count == 0) modes.Add(X11RenderingMode.Software);
        // Software always last, so that the window still opens when the others are not available.
        if (!modes.Contains(X11RenderingMode.Software)) modes.Add(X11RenderingMode.Software);
        RenderingChoice = string.Join(",", modes).ToLowerInvariant();
        return builder.With(new X11PlatformOptions
        {
            RenderingMode = modes,
            // Software: keep the last frame, so that only the changed part is drawn again.
            UseRetainedFramebuffer = modes[0] == X11RenderingMode.Software,
        });
    }
}
