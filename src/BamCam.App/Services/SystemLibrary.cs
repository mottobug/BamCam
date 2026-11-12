using BamCam.Core.Cam;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Services;

/// <summary>
/// Global libraries shared by all drawings (post processors), loaded from the BamCam system folder.
/// Post processors of an existing CamBam installation are offered too, for interoperability.
/// </summary>
public sealed class SystemLibrary
{
    /// <summary>
    /// BamCam's own libraries, post processors, scripts and plugins: %APPDATA%\BamCam, or the folder named by the
    /// environment variable BAMCAM_SYSTEM (another configuration, or a test that must not touch the real one).
    /// </summary>
    public static string SystemFolder { get; } =
        Environment.GetEnvironmentVariable("BAMCAM_SYSTEM") is { Length: > 0 } own ? Path.GetFullPath(own)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BamCam");

    /// <summary>BAMCAM_IGNORE_CAMBAM=1: the data of a CamBam installation is left out (as on a computer without CamBam).</summary>
    private static bool IgnoreCamBam => Environment.GetEnvironmentVariable("BAMCAM_IGNORE_CAMBAM") == "1";

    public static string PostFolder => Path.Combine(SystemFolder, "post");

    /// <summary>Data folders of a local CamBam installation (read-only; used if present).</summary>
    private static IEnumerable<string> CamBamPostFolders()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (IgnoreCamBam || !Directory.Exists(common)) yield break;
        foreach (var dir in Directory.GetDirectories(common, "CamBam*"))
        {
            var post = Path.Combine(dir, "post");
            if (Directory.Exists(post)) yield return post;
        }
    }

    public List<PostProcessorDefinition> Posts { get; } = [];

    /// <summary>Where each post came from (null = built in).</summary>
    public Dictionary<PostProcessorDefinition, string?> PostSources { get; } = [];

    public string DefaultPostName
    {
        get => CamLibraries.DefaultPostProcessor;
        set => CamLibraries.DefaultPostProcessor = value;
    }

    public List<string> LoadMessages { get; } = [];

    /// <summary>Data folders of a local CamBam installation with the given sub-folder (read-only).</summary>
    private static IEnumerable<string> CamBamFolders(string sub)
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (IgnoreCamBam || !Directory.Exists(common)) yield break;
        foreach (var dir in Directory.GetDirectories(common, "CamBam*"))
        {
            var p = Path.Combine(dir, sub);
            if (Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>Data folders of the CamBam installations on this computer.</summary>
    public static IEnumerable<string> CamBamDataFolders()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return !IgnoreCamBam && Directory.Exists(common) ? Directory.GetDirectories(common, "CamBam*") : [];
    }

    public static string PlasmaFolder => Path.Combine(SystemFolder, "plasma");

    private void LoadLibraries()
    {
        CamLibraries.StyleLibraries.Clear();
        CamLibraries.ToolLibraries.Clear();
        LoadCutData();

        // Translation tables of a local CamBam: drawings saved by a localised CamBam contain translated values.
        foreach (var dir in CamBamDataFolders())
            foreach (var file in Directory.GetFiles(dir, "translate_*").Where(f => Path.GetExtension(f).Length == 0))
            {
                try { CbLocalization.LoadTranslationFile(file); }
                catch (Exception ex) { LoadMessages.Add(F("Could not read translations '{0}': {1}", file, ex.Message)); }
            }
        foreach (var sub in new[] { "styles", "tools" })
        {
            var own = Path.Combine(SystemFolder, sub);
            Directory.CreateDirectory(own);
            if (sub == "tools")
                foreach (var template in new[] { CamLibraries.LatheTemplates(false), CamLibraries.LatheTemplates(true) })
                {
                    // Lathe tool templates; an existing file (perhaps edited) is never overwritten.
                    var path = Path.Combine(own, template.Name + ".xml");
                    if (File.Exists(path)) continue;
                    try { LibraryFiles.SaveTools(template, path); }
                    catch (Exception ex) { LoadMessages.Add(F("Could not save tool library '{0}': {1}", template.Name, ex.Message)); }
                }
            foreach (var folder in CamBamFolders(sub).Prepend(own))
            foreach (var file in Directory.GetFiles(folder, "*.xml"))
            {
                try
                {
                    if (sub == "styles")
                    {
                        var lib = LibraryFiles.LoadStyles(file);
                        if (CamLibraries.StyleLibraries.All(l => !l.Name.Equals(lib.Name, StringComparison.OrdinalIgnoreCase)))
                            CamLibraries.StyleLibraries.Add(lib);
                    }
                    else
                    {
                        var lib = LibraryFiles.LoadTools(file);
                        if (CamLibraries.ToolLibraries.All(l => !l.Name.Equals(lib.Name, StringComparison.OrdinalIgnoreCase)))
                            CamLibraries.ToolLibraries.Add(lib);
                    }
                }
                catch (Exception ex)
                {
                    LoadMessages.Add(F("Could not load library '{0}': {1}", file, ex.Message));
                }
            }
        }
    }

    /// <summary>Plasma cut data tables; the starting tables are written once and never overwritten.</summary>
    private void LoadCutData()
    {
        CamLibraries.PlasmaTables.Clear();
        try
        {
            Directory.CreateDirectory(PlasmaFolder);
            foreach (var template in new[] { PlasmaCutTable.Template(false), PlasmaCutTable.Template(true) })
            {
                var path = Path.Combine(PlasmaFolder, template.Name + ".xml");
                if (!File.Exists(path)) LibraryFiles.SaveCutData(template, path);
            }
        }
        catch (Exception ex)
        {
            LoadMessages.Add(F("Could not save cut data table: {0}", ex.Message));
        }
        if (!Directory.Exists(PlasmaFolder)) return;
        foreach (var file in Directory.GetFiles(PlasmaFolder, "*.xml"))
        {
            try { CamLibraries.PlasmaTables.Add(LibraryFiles.LoadCutData(file)); }
            catch (Exception ex) { LoadMessages.Add(F("Could not load library '{0}': {1}", file, ex.Message)); }
        }
    }

    public void Reload()
    {
        Posts.Clear();
        PostSources.Clear();
        LoadMessages.Clear();
        LoadLibraries();
        foreach (var p in PostProcessorDefinition.BuiltIn()) Add(p, null);

        Directory.CreateDirectory(PostFolder);
        foreach (var folder in CamBamPostFolders().Prepend(PostFolder))
        {
            foreach (var file in Directory.GetFiles(folder, "*.cbpp"))
            {
                try
                {
                    var def = CbppFile.Load(file);
                    // Files in the BamCam folder override built-ins; CamBam files are added if the name is free.
                    var existing = Posts.FirstOrDefault(p => p.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        if (folder != PostFolder) continue;
                        Posts.Remove(existing);
                        PostSources.Remove(existing);
                    }
                    Add(def, file);
                }
                catch (Exception ex)
                {
                    LoadMessages.Add(F("Could not load post processor '{0}': {1}", file, ex.Message));
                }
            }
        }
        CamLibraries.PostProcessors.Clear();
        CamLibraries.PostProcessors.AddRange(Posts.Select(p => p.Name));
    }

    private void Add(PostProcessorDefinition def, string? source)
    {
        Posts.Add(def);
        PostSources[def] = source;
    }

    public PostProcessorDefinition Resolve(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var p = Posts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p;
        }
        return Posts.FirstOrDefault(x => x.Name.Equals(DefaultPostName, StringComparison.OrdinalIgnoreCase))
               ?? Posts.First();
    }
}
