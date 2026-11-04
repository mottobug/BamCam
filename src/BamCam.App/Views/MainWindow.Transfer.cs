using Avalonia.Platform.Storage;
using BamCam.App.Services;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private static readonly FilePickerFileType BackupType = new("BamCam backup (.zip)") { Patterns = ["*.zip"] };

    /// <summary>Tools ▸ Import from CamBam: the libraries and post processors of a CamBam data folder, as editable copies.</summary>
    private async void ImportFromCamBam()
    {
        var installed = SystemLibrary.CamBamDataFolders().FirstOrDefault();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("CamBam data folder (with the folders styles, tools and post)"),
            SuggestedStartLocation = installed == null ? null : await StorageProvider.TryGetFolderFromPathAsync(installed),
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } source) return;
        var result = ConfigTransfer.ImportCamBamLibraries(source, SystemLibrary.SystemFolder);
        foreach (var file in result.Failed) Log(F("Could not copy '{0}'", file));
        if (result.Copied.Count == 0 && result.Existing.Count == 0 && result.Failed.Count == 0)
        {
            Log(F("No libraries or post processors found in {0}.", source));
            return;
        }
        ReloadLibraries();
        Log(F("Imported from CamBam: {0} files copied, {1} kept because they exist already or have the name of a built-in post processor.", result.Copied.Count, result.Existing.Count));
    }

    /// <summary>Tools ▸ Export Configuration: settings, libraries, post processors, translations and plugins in one file.</summary>
    private async void ExportConfiguration()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Export Configuration"),
            SuggestedFileName = $"BamCam-configuration-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [BackupType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        SaveSettings();
        try
        {
            ConfigTransfer.Export(SystemLibrary.SystemFolder, _settingsPath, path);
            Log(F("Configuration exported to {0}.", path));
        }
        catch (Exception ex)
        {
            Log(F("Could not save {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>Tools ▸ Import Configuration: restores a backup over the current configuration and applies it.</summary>
    private async void ImportConfiguration()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Import Configuration"),
            AllowMultiple = false,
            FileTypeFilter = [BackupType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var answer = await Dialogs.Ask(this, T("Import Configuration"),
            F("Replace the current settings and libraries with those of\n{0}?\n\nFiles with the same name are overwritten; the current state is saved first. BamCam asks again before it runs scripts, post-build commands and plugins.", path),
            withCancel: false);
        if (answer != true) return;

        SaveSettings();
        ConfigImportResult result;
        try
        {
            result = ConfigTransfer.Import(path, SystemLibrary.SystemFolder, _settingsPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log(F("Configuration not imported: {0}", ex.Message));
            return;
        }

        // Loaded again at once: closing the window saves the settings in memory over the file.
        if (result.SettingsError != null) Log(F("Configuration not imported: {0}", result.SettingsError));
        else _settings = AppSettings.Load(_settingsPath, out _);
        ApplySettings();
        ReloadLibraries();
        Core.Localization.Tr.Reload();
        foreach (var failed in result.Failed) Log(F("Could not copy '{0}'", failed));
        Log(F("Configuration imported: {0} files. The previous state is saved in {1}.", result.Restored, result.SafetyBackup));
        Log(T("Restart BamCam to apply the window layout and the plugins."));
    }
}
