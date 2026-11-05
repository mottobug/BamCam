using System.Reflection;
using BamCam.App.Controls;
using BamCam.App.Drawing;
using BamCam.Core.Cam;
using BamCam.Core.Model;

namespace BamCam.Tests;

[Collection(GlobalLibrariesCollection.Name)]
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

    [Fact]
    public void Post_processor_of_the_drawing_is_chosen_from_a_list()
    {
        var names = CamLibraries.PostProcessors.ToList();
        var defaultName = CamLibraries.DefaultPostProcessor;
        try
        {
            CamLibraries.PostProcessors.Clear();
            CamLibraries.PostProcessors.AddRange(["Default", "RedGear Plasma"]);
            CamLibraries.DefaultPostProcessor = "Default";
            var attribute = typeof(MachiningOptions).GetProperty(nameof(MachiningOptions.PostProcessor))!.GetCustomAttribute<ChoicesAttribute>()!;
            var choices = attribute.GetChoices(typeof(MachiningOptions));
            Assert.Equal(["", "Default", "RedGear Plasma"], choices.Select(c => c.Key));
            Assert.Contains("Default", choices[0].Value);
        }
        finally
        {
            CamLibraries.PostProcessors.Clear();
            CamLibraries.PostProcessors.AddRange(names);
            CamLibraries.DefaultPostProcessor = defaultName;
        }
    }

    [Theory]
    // Z depth only, level 2 chosen: level 1 is already cut, level 3 not yet.
    [InlineData(false, true, 1, 1, 0, 2, ToolpathFilterLook.Cut)]
    [InlineData(false, true, 2, 2, 0, 2, ToolpathFilterLook.Current)]
    [InlineData(false, true, 3, 3, 0, 2, ToolpathFilterLook.Hidden)]
    // Index only, path 5 chosen.
    [InlineData(true, false, 4, 1, 5, 0, ToolpathFilterLook.Cut)]
    [InlineData(true, false, 5, 1, 5, 0, ToolpathFilterLook.Current)]
    [InlineData(true, false, 6, 1, 5, 0, ToolpathFilterLook.Hidden)]
    // Both: a path before the index on a deeper level is not shown.
    [InlineData(true, true, 4, 3, 5, 2, ToolpathFilterLook.Hidden)]
    [InlineData(true, true, 4, 2, 5, 2, ToolpathFilterLook.Cut)]
    public void Filter_shows_what_is_already_cut(bool byIndex, bool byZ, int number, int level, int index, int zLevel, ToolpathFilterLook expected)
    {
        var filter = new ToolpathFilterSettings { IndexEnabled = byIndex, ZEnabled = byZ };
        Assert.Equal(expected, filter.LookOf(number, level, index, zLevel));
    }

    [Fact]
    public void Filter_options_change_the_look_of_cut_and_current_paths()
    {
        var filter = new ToolpathFilterSettings { ZEnabled = true, ShowCut = false, Highlight = false };
        Assert.Equal(ToolpathFilterLook.Hidden, filter.LookOf(1, 1, 0, 2));
        Assert.Equal(ToolpathFilterLook.Normal, filter.LookOf(2, 2, 0, 2));
        Assert.Equal(ToolpathFilterLook.Normal, new ToolpathFilterSettings().LookOf(9, 9, 1, 1));
    }

    [Fact]
    public void A_stored_value_missing_from_the_list_stays_visible()
    {
        IReadOnlyList<KeyValuePair<string, string>> list = [new("", "(default)"), new("GRBL", "GRBL")];
        Assert.Same(list, PropertyGrid.ChoicesWithCurrent(list, ""));
        Assert.Same(list, PropertyGrid.ChoicesWithCurrent(list, "GRBL"));
        var extended = PropertyGrid.ChoicesWithCurrent(list, "Other machine");
        Assert.Equal(3, extended.Count);
        Assert.Equal("Other machine", extended[2].Key);
        Assert.Contains("Other machine", extended[2].Value);
    }
}
