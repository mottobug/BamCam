using Avalonia.Controls;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Model;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private AppSettings _settings = new();
    private string _settingsPath = AppSettings.DefaultPath;
    private bool _screenshotMode;
    private MenuItem? _recentMenu;

    /// <summary>Loads the configuration (path can be overridden with BAMCAM_SETTINGS, e.g. for tests).</summary>
    private void LoadSettings()
    {
        _settingsPath = Environment.GetEnvironmentVariable("BAMCAM_SETTINGS") is { Length: > 0 } p ? p : AppSettings.DefaultPath;
        _settings = AppSettings.Load(_settingsPath, out var error);
        if (error != null) _pendingLogs.Add(error);

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_settings.LeftColumnWidth);
        RightGrid.RowDefinitions[3].Height = new GridLength(_settings.MessagesHeight);
        ApplySettings();
    }

    /// <summary>Messages produced before the message window exists.</summary>
    private readonly List<string> _pendingLogs = [];

    /// <summary>Pushes the configuration into the view, tools and libraries.</summary>
    private void ApplySettings()
    {
        var oldFont = TextOutlines.DefaultFont;
        _settings.ApplyTo(View.Settings, _session?.Document.Units ?? _settings.DrawingUnits);
        DrawTool.RepeatCommands = _settings.RepeatCommands;
        TextOutlines.DefaultFont = _settings.DefaultFontFamily;
        _system.DefaultPostName = _settings.DefaultPostProcessor;
        if (_session != null && oldFont != _settings.DefaultFontFamily)
            foreach (var t in _session.Document.AllEntities().OfType<TextEntity>()) t.InvalidateOutlines();
        if (_checkItems.Count > 0) RefreshChecks();
        View.Invalidate();
    }

    private void SaveSettings()
    {
        if (_screenshotMode) return;
        _settings.CaptureFrom(View.Settings);
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal && Bounds.Width > 0)
        {
            _settings.WindowWidth = Bounds.Width;
            _settings.WindowHeight = Bounds.Height;
        }
        _settings.LeftColumnWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        _settings.MessagesHeight = RightGrid.RowDefinitions[3].ActualHeight;
        try
        {
            _settings.Sanitize();
            _settings.Save(_settingsPath);
        }
        catch (Exception ex)
        {
            Log($"Could not save settings to {_settingsPath}: {ex.Message}");
        }
    }

    /// <summary>A new, empty drawing using the configured units and layer colour.</summary>
    private CadDocument NewDocument()
    {
        var doc = CadDocument.CreateNew();
        doc.Units = _settings.DrawingUnits;
        foreach (var l in doc.Layers) l.Color = _settings.DefaultLayerColor;
        doc.IsModified = false;
        return doc;
    }

    private async void ShowOptions()
    {
        var result = await new OptionsWindow(_settings, _system.Posts.Select(p => p.Name)).ShowFor(this);
        if (result == null) return;
        _settings = result;
        if (Properties.SelectedObject is AppSettings) Properties.SelectedObject = _settings;
        ApplySettings();
        _settings.ApplyGrid(View.Settings, _session.Document.Units);
        BuildSystemTree();
        UpdateTitle();
        SaveSettings();
        Log("Options saved.");
    }

    /// <summary>Called when the configuration is edited in the main property grid (System ▸ Configuration).</summary>
    private void OnSettingsEditedInGrid()
    {
        _settings.Sanitize();
        ApplySettings();
        BuildSystemTree();
        UpdateTitle();
        SaveSettings();
    }

    private void AddRecentFile(string path)
    {
        if (_screenshotMode) return;
        _settings.AddRecentFile(Path.GetFullPath(path));
        SaveSettings();
    }

    private void FillRecentMenu()
    {
        if (_recentMenu == null) return;
        _recentMenu.Items.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
            return;
        }
        var i = 1;
        foreach (var file in _settings.RecentFiles.ToList())
        {
            var item = new MenuItem { Header = $"_{i++} {file.Replace("_", "__")}", IsEnabled = File.Exists(file) };
            item.Click += async (_, _) =>
            {
                if (await ConfirmDiscard()) OpenPath(file);
            };
            _recentMenu.Items.Add(item);
        }
        _recentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear List" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles.Clear();
            SaveSettings();
        };
        _recentMenu.Items.Add(clear);
    }
}
