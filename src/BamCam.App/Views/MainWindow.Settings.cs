using BamCam.Core.Localization;
using static BamCam.Core.Localization.Tr;
using Avalonia.Controls;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Cam;
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
        // Additional or corrected translations: next to the program and in the user's system folder.
        Tr.SearchFolders.Clear();
        Tr.SearchFolders.Add(Path.Combine(AppContext.BaseDirectory, "lang"));
        Tr.SearchFolders.Add(Path.Combine(SystemLibrary.SystemFolder, "lang"));
        _settings = AppSettings.Load(_settingsPath, out var error);
        Tr.SetLanguage(_settings.Language);
        if (error != null) _pendingLogs.Add(error);

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized || !FitsOnScreen()) WindowState = WindowState.Maximized;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_settings.LeftColumnWidth);
        RightGrid.RowDefinitions[3].Height = new GridLength(_settings.MessagesHeight);
        ApplySettings();
    }

    /// <summary>
    /// True when the window (size in device independent units) fits the work area of the primary screen.
    /// Small or highly scaled displays (e.g. 1280x800 at 150 % = 853x501) would otherwise push the
    /// title bar with minimise/maximise/close off the top of the screen; such windows start maximised.
    /// </summary>
    private bool FitsOnScreen()
    {
        if (Screens.Primary is not { } screen) return true;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        // Leave room for the window frame and title bar.
        const double frame = 48;
        var fits = Width + 16 <= area.Width / scaling && Height + frame <= area.Height / scaling;
        if (!fits)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scaling - 16));
            Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scaling - frame));
        }
        return fits;
    }

    /// <summary>Texts of the window itself (the menus, trees and property grid translate while being built).</summary>
    private void ApplyLanguage()
    {
        DrawingTab.Header = T("Drawing");
        SystemTab.Header = T("System");
        MessagesHeader.Text = T("Messages");
        StatusText.Text = T("Ready");
    }

    /// <summary>The language was switched (Options): rebuild everything that shows text.</summary>
    private void OnLanguageChanged()
    {
        ApplyLanguage();
        BuildMenu();
        BuildToolbar();
        BuildSystemTree();
        RebuildTree();
        Properties.Refresh();
        UpdateTitle();
        Log(F("Language: {0}", Tr.Available().FirstOrDefault(l => l.Code == Tr.Language)?.Name ?? Tr.Language));
    }

    /// <summary>
    /// Tools ▸ Edit Language File: the texts of the active language with the user's own overrides
    /// (<c>lang/{code}.json</c> in the system folder). An editor of its own: the Windows 11 Notepad cannot open
    /// files in the AppData folder of other programs.
    /// </summary>
    private void EditLanguageFile()
    {
        var name = Tr.Available().FirstOrDefault(l => l.Code == Tr.Language)?.Name ?? Tr.Language;
        _ = new LanguageEditorWindow(Path.Combine(SystemLibrary.SystemFolder, "lang"), Tr.Language, name).ShowDialog(this);
    }

    /// <summary>Tools ▸ Reload Language File: applies changed language files without a restart.</summary>
    private void ReloadLanguageFile()
    {
        Tr.Reload(); // raises Changed: menus, trees and properties are rebuilt (OnLanguageChanged)
    }

    /// <summary>Messages produced before the message window exists.</summary>
    private readonly List<string> _pendingLogs = [];

    /// <summary>Pushes the configuration into the view, tools and libraries.</summary>
    private void ApplySettings()
    {
        Tr.SetLanguage(_settings.Language);
        var oldFont = TextOutlines.DefaultFont;
        _settings.ApplyTo(View.Settings, _session?.Document.Units ?? _settings.DrawingUnits);
        DrawTool.RepeatCommands = _settings.RepeatCommands;
        TextOutlines.DefaultFont = _settings.DefaultFontFamily;
        SplineEntity.Tolerance = _settings.SplineTolerance;
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
            Log(F("Could not save settings to {0}: {1}", _settingsPath, ex.Message));
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
        Log(T("Options saved."));
    }

    /// <summary>Tools ▸ Speeds and Feeds for the selected machining operation (or without one, to look values up).</summary>
    private async void ShowSpeedsFeeds()
    {
        var (window, op, name) = NewSpeedsFeedsWindow();
        await window.ShowDialog(this);
        SaveSettings();
        if (op == null || window.Applied is not { } r) return;
        _session.Execute(T("Speeds and Feeds"), () =>
        {
            op.SpindleSpeed.Set(r.SpindleSpeed);
            op.CutFeedrate.Set(r.CutFeedrate);
            op.PlungeFeedrate.Set(r.PlungeFeedrate);
        });
        _session.Inspect(op);
        Log(F("{0}: spindle speed {1:0} rpm, cut feedrate {2:0.#}, plunge feedrate {3:0.#}.", name, r.SpindleSpeed, r.CutFeedrate, r.PlungeFeedrate));
    }

    private (SpeedsFeedsWindow Window, MachineOp? Op, string? Name) NewSpeedsFeedsWindow()
    {
        var doc = _session.Document;
        var op = _session.Inspected as MachineOp;
        List<CuttingMaterial> materials;
        var file = Path.Combine(Services.SystemLibrary.SystemFolder, "cutting-materials.json");
        try
        {
            materials = CuttingData.Load(file);
        }
        catch (Exception ex)
        {
            Log(F("Error opening {0}: {1}", file, ex.Message));
            materials = CuttingData.BuiltIn.ToList();
        }
        var diameter = op?.EffectiveToolDiameter(doc) ?? (doc.Machining.ToolDiameter > 0 ? doc.Machining.ToolDiameter : 6);
        var part = op == null ? null : doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
        var tool = op == null ? null : CamLibraries.FindTool(doc, part, op.EffectiveToolNumber(doc), op is TurningOp);
        var name = op == null ? null : string.IsNullOrEmpty(op.Name) ? op.DisplayType : op.Name;
        return (new SpeedsFeedsWindow(materials, _settings.SpeedsFeeds, doc.Units, diameter, tool is { Flutes: > 0 } ? tool.Flutes : 2, name), op, name);
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
            _recentMenu.Items.Add(new MenuItem { Header = T("(empty)"), IsEnabled = false });
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
        var clear = new MenuItem { Header = T("Clear List") };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles.Clear();
            SaveSettings();
        };
        _recentMenu.Items.Add(clear);
    }
}
