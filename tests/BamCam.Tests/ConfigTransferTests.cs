using System.IO.Compression;
using BamCam.App.Services;

namespace BamCam.Tests;

public sealed class ConfigTransferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bamcam-transfer-").FullName;

    private string System => Path.Combine(_dir, "system");
    private string SettingsFile => Path.Combine(System, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string Styles = "<CAMStyles><CAMStyle Name=\"\" /></CAMStyles>";
    private const string Tools = "<ToolLibrary />";

    [Fact]
    public void CamBam_libraries_are_copied_and_existing_ones_kept()
    {
        var cam = Path.Combine(_dir, "CamBam");
        WriteFile(Path.Combine(cam, "styles", "Standard-mm.xml"), Styles);
        WriteFile(Path.Combine(cam, "styles", "Mine.xml"), Styles);
        WriteFile(Path.Combine(cam, "tools", "Default-mm.xml"), Tools);
        WriteFile(Path.Combine(cam, "tools", "Broken.xml"), "not xml");
        var mine = Path.Combine(System, "styles", "Mine.xml");
        WriteFile(mine, "<CAMStyles><!-- edited --></CAMStyles>");

        var result = ConfigTransfer.ImportCamBamLibraries(cam, System);

        Assert.Equal(2, result.Copied.Count);
        Assert.True(File.Exists(Path.Combine(System, "styles", "Standard-mm.xml")));
        Assert.True(File.Exists(Path.Combine(System, "tools", "Default-mm.xml")));
        Assert.Contains("edited", File.ReadAllText(mine));
        Assert.Single(result.Existing);
        Assert.Contains("Broken.xml", Assert.Single(result.Failed));
        Assert.False(File.Exists(Path.Combine(System, "tools", "Broken.xml")));
    }

    [Fact]
    public void Post_processors_are_copied_unless_named_like_a_built_in_one()
    {
        var cam = Path.Combine(_dir, "CamBam");
        WriteFile(Path.Combine(cam, "post", "My Machine.cbpp"), "<PostProcessor />");
        WriteFile(Path.Combine(cam, "post", "mach3.cbpp"), "<PostProcessor />");
        WriteFile(Path.Combine(cam, "post", "Broken.cbpp"), "not xml");

        var result = ConfigTransfer.ImportCamBamLibraries(cam, System);

        Assert.True(File.Exists(Path.Combine(System, "post", "My Machine.cbpp")));
        Assert.False(File.Exists(Path.Combine(System, "post", "mach3.cbpp")));
        Assert.False(File.Exists(Path.Combine(System, "post", "Broken.cbpp")));
        Assert.Single(result.Copied);
        Assert.Contains("mach3.cbpp", Assert.Single(result.Existing));
        Assert.Contains("Broken.cbpp", Assert.Single(result.Failed));
    }

    [Fact]
    public void The_styles_folder_alone_can_be_chosen()
    {
        var styles = Path.Combine(_dir, "CamBam", "styles");
        WriteFile(Path.Combine(styles, "Standard-mm.xml"), Styles);
        Assert.Single(ConfigTransfer.ImportCamBamLibraries(styles, System).Copied);
        Assert.True(File.Exists(Path.Combine(System, "styles", "Standard-mm.xml")));
    }

    [Fact]
    public void Backup_restores_files_on_another_computer_without_the_approvals()
    {
        var settings = new AppSettings { Language = "de", DefaultPostProcessor = "RedGear Plasma" };
        settings.ApprovedScripts.Add("hash");
        settings.ApprovedPostBuildCommands.Add("curl");
        settings.Plugins.Add(new PluginApproval { File = "x.dll", Hash = "h", Enabled = true });
        settings.Save(SettingsFile);
        WriteFile(Path.Combine(System, "post", "Mine.cbpp"), "<PostProcessor />");
        WriteFile(Path.Combine(System, "crash.log"), "secret");
        WriteFile(Path.Combine(System, "backups", "old.zip"), "old");
        var zip = Path.Combine(_dir, "backup.zip");

        ConfigTransfer.Export(System, SettingsFile, zip);

        var other = Path.Combine(_dir, "other");
        var otherSettings = Path.Combine(other, "settings.json");
        WriteFile(Path.Combine(other, "styles", "Stays.xml"), Styles);
        var result = ConfigTransfer.Import(zip, other, otherSettings);

        Assert.Null(result.SettingsError);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(Path.Combine(other, "post", "Mine.cbpp")));
        Assert.True(File.Exists(Path.Combine(other, "styles", "Stays.xml")));
        Assert.False(File.Exists(Path.Combine(other, "crash.log")));
        Assert.False(File.Exists(Path.Combine(other, "backups", "old.zip")));
        Assert.True(File.Exists(result.SafetyBackup));

        var restored = AppSettings.Load(otherSettings, out var error);
        Assert.Null(error);
        Assert.Equal("RedGear Plasma", restored.DefaultPostProcessor);
        Assert.Empty(restored.ApprovedScripts);
        Assert.Empty(restored.ApprovedPostBuildCommands);
        Assert.Empty(restored.Plugins);
    }

    [Fact]
    public void Import_saves_the_current_state_first()
    {
        var zip = Path.Combine(_dir, "backup.zip");
        WriteFile(Path.Combine(System, "post", "A.cbpp"), "backup");
        ConfigTransfer.Export(System, SettingsFile, zip);
        WriteFile(Path.Combine(System, "post", "A.cbpp"), "changed later");

        var result = ConfigTransfer.Import(zip, System, SettingsFile);

        Assert.Equal("backup", File.ReadAllText(Path.Combine(System, "post", "A.cbpp")));
        using var safety = ZipFile.OpenRead(result.SafetyBackup);
        using var reader = new StreamReader(safety.GetEntry("post/A.cbpp")!.Open());
        Assert.Equal("changed later", reader.ReadToEnd());
    }

    [Fact]
    public void Other_zip_files_and_paths_outside_the_folder_are_refused_before_anything_changes()
    {
        var plain = Path.Combine(_dir, "plain.zip");
        using (var zip = ZipFile.Open(plain, ZipArchiveMode.Create)) zip.CreateEntry("post/A.cbpp");
        Assert.Throws<InvalidDataException>(() => ConfigTransfer.Import(plain, System, SettingsFile));

        var evil = Path.Combine(_dir, "evil.zip");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            zip.CreateEntry("bamcam-backup.json");
            zip.CreateEntry("../outside.txt");
        }
        Assert.Throws<InvalidDataException>(() => ConfigTransfer.Import(evil, System, SettingsFile));
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
        Assert.False(Directory.Exists(Path.Combine(System, "backups")));
    }
}
