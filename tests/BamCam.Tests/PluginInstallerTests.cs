using System.IO.Compression;
using BamCam.App.Services;

namespace BamCam.Tests;

/// <summary>Tools ▸ Plugins ▸ Install from ZIP.</summary>
public class PluginInstallerTests
{
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bamcam-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A ZIP named <paramref name="name"/> with the given entries (path, text).</summary>
    private static string Zip(string folder, string name, params (string Path, string Text)[] entries)
    {
        var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    [Theory]
    [InlineData("community-GeoffRoy.Revolve-1.2.0.zip", "GeoffRoy.Revolve")]
    [InlineData("community-GeoffRoy.WrapGCode-2.0.1-beta.3.zip", "GeoffRoy.WrapGCode")]
    [InlineData("MyPlugin.zip", "MyPlugin")]
    [InlineData("Some Plugin 2-1.0.zip", "Some Plugin 2")]
    public void The_folder_is_named_after_the_plugin_without_version(string zip, string folder) =>
        Assert.Equal(folder, PluginInstaller.FolderName(zip));

    [Fact]
    public void A_plugin_zip_is_unpacked_into_a_folder_of_its_own()
    {
        var work = NewFolder();
        var plugins = Path.Combine(work, "plugins");
        var zip = Zip(work, "community-Author.Square-1.0.0.zip", ("Author.Square.dll", "dll"), ("Author.Square.deps.json", "{}"), ("README.md", "# Square"));

        var (outcome, folder) = PluginInstaller.Install(zip, plugins, replace: false);

        Assert.Equal(PluginInstaller.Outcome.Installed, outcome);
        Assert.Equal(Path.Combine(plugins, "Author.Square"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "Author.Square.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
    }

    [Fact]
    public void A_zip_with_one_folder_inside_is_unpacked_without_that_folder()
    {
        var work = NewFolder();
        var zip = Zip(work, "Wrapped.zip", ("Wrapped/Wrapped.dll", "dll"), ("Wrapped/sub/data.txt", "x"));

        var (_, folder) = PluginInstaller.Install(zip, work, replace: false);

        Assert.True(File.Exists(Path.Combine(folder, "Wrapped.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "sub", "data.txt")));
    }

    [Fact]
    public void A_zip_without_a_dll_or_with_paths_leaving_the_folder_is_refused()
    {
        var work = NewFolder();
        Assert.Throws<InvalidDataException>(() => PluginInstaller.Install(Zip(work, "Text.zip", ("readme.txt", "x")), work, replace: false));
        Assert.Throws<InvalidDataException>(() => PluginInstaller.Install(Zip(work, "Evil.zip", ("Evil.dll", "x"), ("../outside.txt", "x")), work, replace: false));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(work)!, "outside.txt")));
    }

    [Fact]
    public void An_installed_plugin_is_replaced_only_when_asked_and_not_while_in_use()
    {
        var work = NewFolder();
        var plugins = Path.Combine(work, "plugins");
        PluginInstaller.Install(Zip(work, "community-A.B-1.0.0.zip", ("A.B.dll", "old")), plugins, replace: false);
        var newer = Zip(work, "community-A.B-1.1.0.zip", ("A.B.dll", "new"));

        Assert.Equal(PluginInstaller.Outcome.Exists, PluginInstaller.Install(newer, plugins, replace: false).Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "A.B", "A.B.dll")));

        // A loaded plugin keeps its DLL open: nothing is touched.
        using (new FileStream(Path.Combine(plugins, "A.B", "A.B.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(PluginInstaller.Outcome.InUse, PluginInstaller.Install(newer, plugins, replace: true).Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "A.B", "A.B.dll")));

        Assert.Equal(PluginInstaller.Outcome.Replaced, PluginInstaller.Install(newer, plugins, replace: true).Outcome);
        Assert.Equal("new", File.ReadAllText(Path.Combine(plugins, "A.B", "A.B.dll")));
    }
}
