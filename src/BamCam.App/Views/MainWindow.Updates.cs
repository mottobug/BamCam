using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BamCam.App.Services;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>A newer release found by the update check (shown in the Help menu), or null.</summary>
    private ReleaseInfo? _availableUpdate;

    /// <summary>The running version ("0.6.0"), from the version the release was built with.</summary>
    private static string CurrentVersion =>
        (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];

    /// <summary>
    /// At start: asks once whether BamCam may check for updates; then checks at most once a day in the background.
    /// Failures (offline, timeout) are silent.
    /// </summary>
    private async Task StartupUpdateCheck()
    {
        if (_screenshotMode) return;
        if (!_settings.UpdateCheckAsked)
        {
            var answer = await Dialogs.Ask(this, T("Updates"),
                T("Should BamCam check once a day at start whether a new version is available?\n\nOnly the list of versions is requested from GitHub; nothing about you or your drawings is sent. You can change this in Tools > Options."),
                withCancel: false);
            _settings.UpdateCheckAsked = true;
            _settings.CheckForUpdates = answer == true;
            SaveSettings();
        }
        if (!_settings.CheckForUpdates) return;
        if (_settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
        await CheckForUpdates(manual: false);
    }

    /// <summary>Asks GitHub for the newest release; Help ▸ Check for Updates shows the result in any case.</summary>
    private async Task CheckForUpdates(bool manual)
    {
        ReleaseInfo? newest;
        try
        {
            newest = await UpdateCheck.FetchNewestAsync(CurrentVersion, _settings.UpdatePrereleases, TimeSpan.FromSeconds(manual ? 15 : 5));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (manual) await Dialogs.Message(this, T("Check for Updates"), F("The update check failed: {0}", ex.Message));
            return;
        }
        _settings.LastUpdateCheck = DateTime.UtcNow;
        SaveSettings();
        // A manual check also offers a version skipped before.
        if (newest != null && UpdateCheck.IsUpdate(newest, CurrentVersion, manual ? null : _settings.SkippedVersion))
        {
            _availableUpdate = newest;
            BuildMenu();
            Log(F("BamCam {0} is available (installed: {1}): Help > Update to {0}.", newest.Version, CurrentVersion));
            if (manual) ShowUpdate();
        }
        else if (manual)
        {
            await Dialogs.Message(this, T("Check for Updates"), F("BamCam {0} is the newest version.", CurrentVersion));
        }
    }

    /// <summary>Window with the release notes of the available update: download page, skip, close.</summary>
    private void ShowUpdate()
    {
        if (_availableUpdate is not { } release) return;
        var window = new Window
        {
            Title = F("BamCam {0}", release.Version),
            Width = 560, Height = 440, MinWidth = 380, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var header = new TextBlock
        {
            Text = F("BamCam {0} is available (installed: {1}){2}.", release.Version, CurrentVersion,
                release.Published is { } d ? ", " + F("released {0}", d.LocalDateTime.ToString("d", Culture)) : ""),
            Margin = new Thickness(10, 10, 10, 6), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        var notes = new TextBox
        {
            Text = release.Notes.Length > 0 ? release.Notes : T("(no release notes)"),
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(10, 0),
        };
        var download = new Button { Content = T("Open Download Page"), IsDefault = true };
        download.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = release.PageUrl, UseShellExecute = true }); }
            catch (Exception ex) { Log(F("Could not open {0}: {1}", release.PageUrl, ex.Message)); }
            window.Close();
        };
        var skip = new Button { Content = T("Skip This Version") };
        skip.Click += (_, _) =>
        {
            _settings.SkippedVersion = release.Version;
            SaveSettings();
            _availableUpdate = null;
            BuildMenu();
            window.Close();
        };
        var close = new Button { Content = T("Close"), IsCancel = true };
        close.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        buttons.Children.Add(download);
        buttons.Children.Add(skip);
        buttons.Children.Add(close);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(notes);
        window.Content = root;
        _ = window.ShowDialog(this);
    }

    /// <summary>Help menu entries of the update check.</summary>
    private object[] UpdateMenuItems()
    {
        var items = new List<object>();
        if (_availableUpdate is { } release) items.Add(Item(F("_Update to {0}...", release.Version), ShowUpdate));
        items.Add(Item("Check for _Updates...", () => _ = CheckForUpdates(manual: true)));
        return items.ToArray();
    }
}
