using BamCam.App.Controls;

namespace BamCam.Tests;

public class PropertyGridTests
{
    [Theory]
    [InlineData("6, 9, 13", new[] { 6, 9, 13 })]
    [InlineData("{6;9 13}", new[] { 6, 9, 13 })]
    [InlineData("13,6,13", new[] { 13, 6 })]
    [InlineData("", new int[0])]
    public void Id_lists_are_parsed(string text, int[] expected) =>
        Assert.Equal(expected, PropertyGrid.ParseIds(text));

    [Theory]
    [InlineData("6, x")]
    [InlineData("-1")]
    public void Invalid_id_lists_are_rejected(string text) =>
        Assert.Throws<FormatException>(() => PropertyGrid.ParseIds(text));

    [Fact]
    public void General_comes_first_and_tool_last()
    {
        int Rank(string c) => PropertyGrid.CategoryRank(c, 0);
        Assert.True(Rank("General") < Rank("Depth"));
        Assert.True(Rank("Depth") < Rank("Cutting"));
        Assert.True(Rank("Cutting") < Rank("Feedrates"));
        Assert.True(Rank("Feedrates") < Rank("Tool"));
        Assert.True(Rank("Display") < Rank("Tool"));
        Assert.True(Rank("Tool") < Rank("Misc"));
    }
}
