using Avalonia;

namespace BamCam.App;

internal static class Program
{
    /// <summary>The Linux rendering modes asked for with --rendering or BAMCAM_RENDERING, or null for Avalonia's default.</summary>
    internal static string? RenderingChoice { get; private set; }

    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    private static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        // Linux: how windows are drawn. Avalonia uses OpenGL (GLX) and falls back to software; with some graphics
        // drivers, often without a compositor (e.g. on LinuxCNC machines), OpenGL flickers and shows what is behind the
        // window. --rendering software (or BAMCAM_RENDERING=software) draws with the CPU instead. Several modes with
        // commas are tried in order: glx, egl, vulkan, software.
        var index = Array.IndexOf(args, "--rendering");
        RenderingChoice = index >= 0 && index + 1 < args.Length ? args[index + 1] : Environment.GetEnvironmentVariable("BAMCAM_RENDERING");
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(RenderingChoice))
        {
            var modes = RenderingChoice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => Enum.TryParse<X11RenderingMode>(m, ignoreCase: true, out var mode) ? mode : (X11RenderingMode?)null)
                .OfType<X11RenderingMode>().ToList();
            if (modes.Count > 0)
            {
                // Software always last, so that the window still opens when the others are not available.
                if (!modes.Contains(X11RenderingMode.Software)) modes.Add(X11RenderingMode.Software);
                builder = builder.With(new X11PlatformOptions
                {
                    RenderingMode = modes,
                    // Software: keep the last frame, so that only the changed part is drawn again.
                    UseRetainedFramebuffer = modes[0] == X11RenderingMode.Software,
                });
            }
            else RenderingChoice = null;
        }
        return builder;
    }
}
