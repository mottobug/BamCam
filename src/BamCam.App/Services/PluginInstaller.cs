using System.IO.Compression;
using System.Text.RegularExpressions;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Services;

/// <summary>
/// Tools ▸ Plugins ▸ Install from ZIP: unpacks a plugin ZIP (e.g. a community plugin from a release) into a folder of
/// its own below the user's plugin folder. BamCam asks at the next start whether the plugin may be loaded, as for any
/// plugin copied there by hand.
/// </summary>
public static partial class PluginInstaller
{
    public enum Outcome { Installed, Replaced, Exists, InUse }

    /// <summary>
    /// The folder name for a ZIP file: "community-GeoffRoy.Revolve-1.2.0.zip" gives "GeoffRoy.Revolve" (prefix and version
    /// dropped, so that a newer version replaces the older one).
    /// </summary>
    public static string FolderName(string zipPath)
    {
        var name = Path.GetFileNameWithoutExtension(zipPath);
        if (name.StartsWith("community-", StringComparison.OrdinalIgnoreCase)) name = name["community-".Length..];
        name = VersionSuffix().Replace(name, "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().Length > 0 ? name.Trim() : "plugin";
    }

    [GeneratedRegex(@"-\d+(\.\d+)*(-[0-9A-Za-z.]+)?$")]
    private static partial Regex VersionSuffix();

    /// <summary>
    /// Unpacks <paramref name="zipPath"/> into <paramref name="pluginFolder"/>/<see cref="FolderName"/>. An existing folder
    /// is replaced only with <paramref name="replace"/> (else <see cref="Outcome.Exists"/>); when its files are in use
    /// (the plugin is loaded) nothing changes (<see cref="Outcome.InUse"/>). Throws <see cref="InvalidDataException"/>
    /// for a ZIP without a DLL or with paths that would leave the folder.
    /// </summary>
    public static (Outcome Outcome, string Folder) Install(string zipPath, string pluginFolder, bool replace)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList(); // folders have no name
        if (!files.Any(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(T("The ZIP file contains no plugin (no .dll file)."));
        foreach (var e in files)
        {
            var p = e.FullName.Replace('\\', '/');
            if (p.StartsWith('/') || p.Contains(':') || p.Split('/').Contains(".."))
                throw new InvalidDataException(F("The ZIP file contains a path outside its folder: {0}", e.FullName));
        }
        // A ZIP holding one folder with everything in it: its content, not the folder.
        var first = files[0].FullName.Replace('\\', '/');
        var wrapper = first.Contains('/') ? first[..(first.IndexOf('/') + 1)] : null;
        if (wrapper != null && !files.All(e => e.FullName.Replace('\\', '/').StartsWith(wrapper, StringComparison.Ordinal))) wrapper = null;

        var target = Path.Combine(pluginFolder, FolderName(zipPath));
        var existed = Directory.Exists(target);
        if (existed)
        {
            if (!replace) return (Outcome.Exists, target);
            // A loaded plugin locks its DLL: find out first, so that the folder is not left half deleted.
            if (Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any(InUse)) return (Outcome.InUse, target);
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (Outcome.InUse, target);
            }
        }
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var e in files)
        {
            var relative = e.FullName.Replace('\\', '/');
            if (wrapper != null) relative = relative[wrapper.Length..];
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(F("The ZIP file contains a path outside its folder: {0}", e.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            e.ExtractToFile(destination, overwrite: true);
        }
        return (existed ? Outcome.Replaced : Outcome.Installed, target);
    }

    private static bool InUse(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
