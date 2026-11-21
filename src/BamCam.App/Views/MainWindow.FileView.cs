using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BamCam.App.Services;
using BamCam.IO.Cb;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>File ▸ New from Template, View ▸ Zoom Actual Size and Window Opacity, Machining ▸ Browse Gcode Folder.</summary>
public partial class MainWindow
{
    /// <summary>Where File ▸ New from Template starts looking: the templates folder in the system folder.</summary>
    public static string TemplateFolder => Path.Combine(SystemLibrary.SystemFolder, "templates");

    /// <summary>An untitled copy of the drawing <paramref name="path"/>; null when it cannot be read.</summary>
    private CadDocument? FromTemplate(string path)
    {
        try
        {
            var doc = CbReader.Load(path);
            doc.FilePath = null;
            doc.Name = "Untitled";
            doc.IsModified = false;
            return doc;
        }
        catch (Exception ex)
        {
            Log(F("Could not read {0}: {1}", path, ex.Message));
            return null;
        }
    }

    /// <summary>File ▸ New from Template: an untitled copy of a chosen drawing; the template itself is never changed.</summary>
    private async void NewFromTemplate()
    {
        if (!await ConfirmDiscard()) return;
        Directory.CreateDirectory(TemplateFolder);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("New from Template"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(TemplateFolder),
            FileTypeFilter = [CbType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (FromTemplate(path) is not { } doc) return;
        _session.Replace(doc);
        Log(F("New drawing from the template {0}.", path));
    }

    /// <summary>
    /// View ▸ Zoom Actual Size: 1 mm of the drawing as 1 mm on the screen, taking the screen as 96 units per inch
    /// (scaled as the system scales); on most screens this is close to the real size.
    /// </summary>
    private void ZoomActualSize()
    {
        var unitsPerInch = _session.Document.Units == DrawingUnits.Inches ? 1 : 25.4;
        View.ZoomTo(96 / unitsPerInch);
    }

    private double _opacityPercent = 100;

    /// <summary>
    /// View ▸ Window Opacity: lets the windows behind shine through, to trace over a picture or drawing in another
    /// program. Not stored: every start begins opaque.
    /// </summary>
    private async Task SetWindowOpacity()
    {
        if (await Dialogs.PromptNumber(this, T("Window Opacity"), T("Opacity in % (10 to 100):"), _opacityPercent) is not { } percent) return;
        _opacityPercent = Math.Clamp(percent, 10, 100);
        TransparencyLevelHint = _opacityPercent < 100 ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
        Opacity = _opacityPercent / 100;
        if (_opacityPercent < 100 && ActualTransparencyLevel == WindowTransparencyLevel.None)
            Log(T("Window Opacity: this system does not let the windows behind shine through."));
    }

    /// <summary>Machining ▸ Browse Gcode Folder: opens the folder of the G-code file in the file manager.</summary>
    private void BrowseGCodeFolder()
    {
        var doc = _session.Document;
        var file = _lastGCodePath ?? (string.IsNullOrWhiteSpace(doc.Machining.OutFile) ? null : ExpandOutFile(doc.Machining.OutFile));
        var folder = file != null && Path.IsPathRooted(file) ? Path.GetDirectoryName(file)
            : doc.FilePath != null ? Path.GetDirectoryName(doc.FilePath) : null;
        if (folder == null || !Directory.Exists(folder))
        {
            Log(folder == null ? T("No G-code folder yet: save the drawing or use Produce Gcode first.")
                : F("The G-code folder {0} does not exist yet.", folder));
            return;
        }
        OpenFolder(folder);
    }
}
