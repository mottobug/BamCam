using BamCam.Plugins;

namespace BamCam.Example;

/// <summary>
/// Plugin demos: one menu entry for each way of writing a plugin, each in its own file, so that a plugin author can
/// look up the one they need and copy it.
/// <list type="number">
/// <item>Demo1_SimpleCommand.cs: a command that asks one or two numbers (AskNumber) and draws.</item>
/// <item>Demo2_AskValues.cs: many values in one window without designing it (AskValues), with a preview.</item>
/// <item>Demo3_OwnWindow.cs: a window of your own layout (Avalonia).</item>
/// <item>Demo4_MouseTool.cs: an interactive tool that picks points with the mouse, with a toolbar button.</item>
/// <item>Demo5_ChangeSelection.cs: changing the selected objects as one undo step; commands sharing a toolbar button.</item>
/// <item>Demo6_ParametricObject.cs: an object type computed from values, edited by double-click.</item>
/// <item>Demo7_MachiningOperation.cs: a machining operation with its own toolpaths.</item>
/// <item>Demo8_FileFormat.cs: File ▸ Open and File ▸ Export for a file type.</item>
/// <item>Demo9_GCode.cs: reading the G-code of the drawing, and a post processor macro.</item>
/// </list>
/// docs/plugins.md describes all of it.
/// </summary>
public sealed class Plugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        // The plugin's own texts in the user interface language: German or English.
        var german = host.Language == "de";
        Demo1_SimpleCommand.Register(host, german);
        Demo2_AskValues.Register(host, german);
        Demo3_OwnWindow.Register(host, german);
        Demo4_MouseTool.Register(host, german);
        Demo5_ChangeSelection.Register(host, german);
        Demo6_ParametricObject.Register(host, german);
        Demo7_MachiningOperation.Register(host, german);
        Demo8_FileFormat.Register(host, german);
        Demo9_GCode.Register(host, german);
    }
}
