using BamCam.Core.Post;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Reading and writing .cbpp post processors (issue 34).</summary>
public class CbppFileTests
{
    [Fact]
    public void Thc_templates_are_read_under_their_element_names_and_their_grid_names()
    {
        var byElement = CbppFile.Parse("<PostProcessor><ThcOn>H1</ThcOn><ThcOff>H0</ThcOff></PostProcessor>");
        Assert.Equal("H1", byElement.ThcOn);
        Assert.Equal("H0", byElement.ThcOff);

        var byGridName = CbppFile.Parse("<PostProcessor><TorchHeightControlOn>H1</TorchHeightControlOn><TorchHeightControlOff>H0</TorchHeightControlOff></PostProcessor>");
        Assert.Equal("H1", byGridName.ThcOn);
        Assert.Equal("H0", byGridName.ThcOff);
        Assert.Empty(byGridName.Unknown);
    }

    [Fact]
    public void Saved_post_keeps_edited_multi_line_templates_and_unknown_elements()
    {
        var post = CbppFile.Parse("<PostProcessor><TorchOff>M5</TorchOff><Custom>x</Custom></PostProcessor>");
        post.TorchOff = "M5\nG4 P15";
        post.TorchProbe = "G38.2 {$z} {$f}\nG91 G38.4 Z12.7 F508\nG90";
        post.ThcOn = "H1";
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-test-{Guid.NewGuid():N}.cbpp");
        try
        {
            CbppFile.Save(post, path);
            var back = CbppFile.Load(path);
            Assert.Equal("M5\nG4 P15", back.TorchOff.Replace("\r\n", "\n"));
            Assert.Equal(post.TorchProbe, back.TorchProbe.Replace("\r\n", "\n"));
            Assert.Equal("H1", back.ThcOn);
            Assert.Equal("x", back.Unknown["Custom"]);
            Assert.Contains("<ThcOn>", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
