using System.ComponentModel;
using System.IO.Compression;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Services;

/// <summary>Result of copying the libraries of a CamBam data folder.</summary>
public sealed record CamBamImportResult(List<string> Copied, List<string> Existing, List<string> Failed);

/// <summary>Result of restoring a configuration backup.</summary>
public sealed record ConfigImportResult(
    [property: Browsable(false)] int Restored,
    [property: Browsable(false)] List<string> Failed,
    [property: Browsable(false)] string SafetyBackup,
    [property: Browsable(false)] string? SettingsError);

/// <summary>
/// Moves configuration between computers: libraries of a CamBam data folder into BamCam's own folders, and the whole
/// system folder and settings to and from a backup file (a zip archive).
/// </summary>
public static class ConfigTransfer
{
    /// <summary>Marks a zip archive as a BamCam backup.</summary>
    private const string Marker = "bamcam-backup.json";
    private const string SettingsEntry = "settings.json";
    private const string BackupFolder = "backups";

    /// <summary>
    /// Copies the style and tool libraries and the post processors of a CamBam data folder (or of its "styles", "tools"
    /// or "post" folder alone). Existing files are kept: they may have been edited. So are post processors named like a
    /// built-in one, which a file in BamCam's folder would replace. Files that cannot be read are not copied.
    /// </summary>
    public static CamBamImportResult ImportCamBamLibraries(string source, string systemFolder)
    {
        var result = new CamBamImportResult([], [], []);
        string[] all = ["styles", "tools", "post"];
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var chosen = all.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        var builtIn = PostProcessorDefinition.BuiltIn().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in chosen != null ? [chosen] : all)
        {
            var folder = chosen != null ? source : Path.Combine(source, sub);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder, sub == "post" ? "*.cbpp" : "*.xml"))
            {
                var target = Path.Combine(systemFolder, sub, Path.GetFileName(file));
                if (File.Exists(target) || (sub == "post" && builtIn.Contains(Path.GetFileNameWithoutExtension(file))))
                {
                    result.Existing.Add(file);
                    continue;
                }
                try
                {
                    if (sub == "styles") LibraryFiles.LoadStyles(file);
                    else if (sub == "tools") LibraryFiles.LoadTools(file);
                    else CbppFile.Load(file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                    result.Copied.Add(target);
                }
                catch (Exception ex)
                {
                    result.Failed.Add($"{file}: {ex.Message}");
                }
            }
        }
        return result;
    }

    /// <summary>Writes the system folder and the settings to a backup file. Crash logs and older backups stay out.</summary>
    public static void Export(string systemFolder, string settingsPath, string zipPath)
    {
        // Written beside the target first, so that a failure leaves an existing backup intact.
        var tmp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var marker = zip.CreateEntry(Marker);
            using (var writer = new StreamWriter(marker.Open()))
                writer.Write($"{{ \"created\": \"{DateTime.UtcNow:O}\" }}");
            if (File.Exists(settingsPath)) zip.CreateEntryFromFile(settingsPath, SettingsEntry);
            if (Directory.Exists(systemFolder))
                foreach (var file in Directory.GetFiles(systemFolder, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(systemFolder, file).Replace('\\', '/');
                    if (relative is SettingsEntry or "crash.log" || relative.StartsWith(BackupFolder + "/", StringComparison.OrdinalIgnoreCase) ||
                        relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    zip.CreateEntryFromFile(file, relative);
                }
        }
        File.Move(tmp, zipPath, overwrite: true);
    }

    /// <summary>
    /// Restores a backup over the current configuration: files of the backup replace those of the same name, others
    /// stay. The current state is saved in the "backups" folder first. Approvals of scripts, post-build commands and
    /// plugins are not taken over, whoever made the file: BamCam asks again before it runs any of them.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a BamCam backup or names a path outside the folder.</exception>
    public static ConfigImportResult Import(string zipPath, string systemFolder, string settingsPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry(Marker) == null) throw new InvalidDataException(F("{0} is not a BamCam configuration backup.", zipPath));

        // Checked before anything changes: an entry like ../../x must not write outside the system folder.
        var root = Path.GetFullPath(systemFolder) + Path.DirectorySeparatorChar;
        var files = new List<(ZipArchiveEntry Entry, string Target)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName is Marker or SettingsEntry || entry.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(F("{0} is not a BamCam configuration backup.", zipPath));
            files.Add((entry, target));
        }

        var safety = Path.Combine(systemFolder, BackupFolder, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(safety)!);
        Export(systemFolder, settingsPath, safety);

        var failed = new List<string>();
        var restored = 0;
        foreach (var (entry, target) in files)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A plugin in use cannot be replaced; the rest is still restored.
                failed.Add($"{entry.FullName}: {ex.Message}");
            }
        }
        return new ConfigImportResult(restored, failed, safety, RestoreSettings(zip, settingsPath));
    }

    /// <summary>Takes over the settings of the backup without its approvals; returns an error text or null.</summary>
    private static string? RestoreSettings(ZipArchive zip, string settingsPath)
    {
        if (zip.GetEntry(SettingsEntry) is not { } entry) return null;
        var tmp = settingsPath + ".import.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            entry.ExtractToFile(tmp, overwrite: true);
            var settings = AppSettings.Load(tmp, out var error);
            if (error != null) return error;
            settings.ApprovedScripts.Clear();
            settings.ApprovedPostBuildCommands.Clear();
            settings.Plugins.Clear();
            settings.Save(settingsPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
