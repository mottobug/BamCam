using BamCam.App.Controls;
using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.IO.Cb;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>
/// Choosing tools and styles in the properties: drop-down lists of the style libraries, the styles and the tools of the
/// library that applies, and questions where a choice disagrees with values set elsewhere. BamCam asks instead of
/// deciding silently, and stores the answer in the operation (issues 4 and 8, CNC-aus-Holz forum post 29).
/// </summary>
public partial class MainWindow
{
    /// <summary>Asked once per session whether to create the missing default tool library.</summary>
    private bool _defaultToolLibraryAsked;

    private void SetupToolAndStyleChoices()
    {
        Properties.DynamicChoices = (target, property) =>
        {
            var doc = _session.Document;
            return (target, property) switch
            {
                (Part or MachiningOptions, nameof(Part.ToolLibrary)) => CamLibraries.ToolLibraryChoices(doc),
                (Part or MachiningOptions, nameof(Part.StyleLibrary)) => CamLibraries.StyleLibraryChoices(doc),
                (MachineOp op, nameof(MachineOp.Style)) => CamLibraries.StyleChoices(CamLibraries.FindStyleLibrary(doc, PartOf(op)),
                    T("(none: the style of the part)")),
                (Part part, nameof(Part.Style)) => CamLibraries.StyleChoices(CamLibraries.FindStyleLibrary(doc, part),
                    T("(none: the style of Machining)")),
                (MachiningOptions, nameof(MachiningOptions.Style)) => CamLibraries.StyleChoices(CamLibraries.FindStyleLibrary(doc, null),
                    T("(default style)")),
                _ => null,
            };
        };
        Properties.DynamicSuggestions = (target, property) =>
        {
            if (property != nameof(MachineOp.ToolNumber)) return null;
            var doc = _session.Document;
            var (part, lathe) = target switch
            {
                MachineOp op => (PartOf(op), op is TurningOp),
                Part p => (p, false),
                MachiningOptions => ((Part?)null, false),
                _ => ((Part?)null, false),
            };
            if (target is not (MachineOp or Part or MachiningOptions)) return null;
            var tools = CamLibraries.AvailableTools(doc, part, lathe);
            if (tools.Count == 0) return null;
            var multipleLibraries = tools.Select(t => t.Library).Distinct().Count() > 1;
            return tools.Select(t => new KeyValuePair<object, string>(t.Tool.Index,
                CamLibraries.ToolLabel(t.Tool) + (multipleLibraries ? $"  [{t.Library}]" : ""))).ToList();
        };
    }

    private Part? PartOf(MachineOp op) => _session.Document.Parts.FirstOrDefault(p => p.Operations.Contains(op));

    /// <summary>The operations a part or the machining options pass their tool and style on to.</summary>
    private List<MachineOp> OperationsOf(object owner) => owner switch
    {
        Part part => [.. part.Operations],
        MachiningOptions => [.. _session.Document.Parts.SelectMany(p => p.Operations)],
        _ => [],
    };

    /// <summary>
    /// After a tool or style was chosen in the properties: the questions where the choice disagrees with values typed
    /// or set elsewhere. The answers are stored in the operations, then the styles are applied again.
    /// </summary>
    private async Task AskAboutToolAndStyle(string key, object target)
    {
        try
        {
            await AskAboutToolAndStyleCore(key, target);
        }
        catch (Exception ex)
        {
            // Started without waiting for it: an error would otherwise vanish.
            ReportError(ex);
        }
    }

    private async Task AskAboutToolAndStyleCore(string key, object target)
    {
        var doc = _session.Document;
        var changed = false;
        switch (target, key)
        {
            case (MachineOp op, nameof(MachineOp.ToolNumber)):
                await AskForMissingToolLibrary(PartOf(op));
                changed |= await AskTypedToolValues(op);
                break;
            case (MachineOp op, nameof(MachineOp.ToolDiameter) or nameof(MachineOp.ToolProfile)):
                // Typed while a tool is chosen: the typed value is meant, but say that the tool has another one.
                changed |= await AskTypedToolValues(op, justTyped: true);
                break;
            case (MachineOp op, nameof(MachineOp.Style)):
                changed |= await AskStyleConflicts(T("this operation"), [op]);
                changed |= await AskStyleTool([op]);
                changed |= await AskTypedToolValues(op);
                break;
            case (Part or MachiningOptions, nameof(Part.ToolNumber)):
                await AskForMissingToolLibrary(target as Part);
                // Operations with a number of their own keep it; the others follow the new number unless their style sets one.
                changed |= await AskStyleTool(OperationsOf(target).Where(o => o.ToolNumber.State != ParamState.Value).ToList());
                break;
            case (Part or MachiningOptions, nameof(Part.Style)):
                // Operations without a style of their own (and, for the machining options, in parts without one) follow it.
                var following = OperationsOf(target).Where(o => string.IsNullOrEmpty(o.Style)
                    && (target is Part || string.IsNullOrEmpty(PartOf(o)?.Style))).ToList();
                changed |= await AskStyleConflicts(target is Part p ? F("the operations of {0}", p.Name) : T("the operations"), following);
                changed |= await AskStyleTool(following);
                break;
        }
        if (!changed) return;
        foreach (var op in doc.Parts.SelectMany(p => p.Operations)) LibraryFiles.ApplyStyle(doc, op);
        _session.NotifyChanged();
        Properties.Refresh();
    }

    /// <summary>
    /// A tool was chosen and the operation has a diameter or profile typed in, or they were just typed while a tool is
    /// chosen: take the tool's or keep them? (<paramref name="justTyped"/>: keeping them is the first answer.)
    /// </summary>
    private async Task<bool> AskTypedToolValues(MachineOp op, bool justTyped = false)
    {
        var doc = _session.Document;
        var number = op.EffectiveToolNumber(doc);
        if (number <= 0 || CamLibraries.FindTool(doc, PartOf(op), number, op is TurningOp) is not { } tool) return false;
        var typed = new List<string>();
        if (op.ToolDiameter.State == ParamState.Value && Math.Abs(op.ToolDiameter.Value - tool.Diameter) > 1e-9)
            typed.Add(F("Tool Diameter {0}", op.ToolDiameter.Value));
        if (op.ToolProfile.State == ParamState.Value && op.ToolProfile.Value != tool.ToolProfile && tool.ToolProfile != ToolProfile.Unspecified)
            typed.Add(F("Tool Profile {0}", T(op.ToolProfile.Value.ToString())));
        if (typed.Count == 0) return false;
        var question = F("{0} uses tool {1}. Typed in the operation: {2}.", op.Name, CamLibraries.ToolLabel(tool), string.Join(", ", typed));
        var take = justTyped
            ? await Dialogs.Choose(this, T("Tool"), question, T("Keep the typed values"), T("Use the values of the tool")) == 1
            : await Dialogs.Choose(this, T("Tool"), question, T("Use the values of the tool"), T("Keep the typed values")) == 0;
        if (!take) return false;
        CamLibraries.FollowToolNumber(doc, op);
        Log(F("{0}: diameter and profile of tool {1} taken.", op.Name, number));
        return true;
    }

    /// <summary>A style was chosen and operations have values typed in that it sets differently: take the style's or keep them?</summary>
    private async Task<bool> AskStyleConflicts(string who, List<MachineOp> ops)
    {
        var doc = _session.Document;
        var conflicts = ops.Select(o => (Op: o, Names: LibraryFiles.StyleConflicts(doc, o))).Where(c => c.Names.Count > 0).ToList();
        if (conflicts.Count == 0) return false;
        string Names(List<string> names) => string.Join(", ", names.Select(n => T(PropertyGrid.Humanize(n))));
        var list = string.Join("\n", conflicts.Take(12).Select(c => $"• {c.Op.Name}: {Names(c.Names)}"));
        if (conflicts.Count > 12) list += "\n" + F("… and {0} more operations", conflicts.Count - 12);
        var answer = await Dialogs.Choose(this, T("CAM Style"),
            F("The style sets these values differently from what is typed in {0}:\n{1}", who, list),
            T("Use the values of the style"), T("Keep the typed values"));
        if (answer != 0) return false;
        foreach (var (op, names) in conflicts) LibraryFiles.TakeStyleValues(doc, op, names);
        Log(F("Style values taken in {0} operation(s).", conflicts.Count));
        return true;
    }

    /// <summary>
    /// The style of operations sets a tool number and the operation, its part or the machining options set another one:
    /// which should they use? The answer becomes the tool number of the operations.
    /// </summary>
    private async Task<bool> AskStyleTool(List<MachineOp> ops)
    {
        var doc = _session.Document;
        var conflicts = new List<(MachineOp Op, int StyleNumber, int Other, string Source)>();
        foreach (var op in ops)
        {
            var styleNumber = LibraryFiles.StyleToolNumber(doc, op);
            if (styleNumber <= 0) continue;
            var part = PartOf(op);
            var (other, source) = op.ToolNumber is { State: ParamState.Value, Value: > 0 } ? (op.ToolNumber.Value, T("the operation"))
                : part is { ToolNumber: > 0 } ? (part.ToolNumber, F("part {0}", part.Name))
                : doc.Machining.ToolNumber > 0 ? (doc.Machining.ToolNumber, T("Machining"))
                : (0, "");
            if (other > 0 && other != styleNumber) conflicts.Add((op, styleNumber, other, source));
        }
        var changed = false;
        foreach (var group in conflicts.GroupBy(c => (c.StyleNumber, c.Other, c.Source)))
        {
            var (styleNumber, other, source) = group.Key;
            var first = group.First().Op;
            var styleDiameter = LibraryFiles.StyleToolDiameter(doc, first);
            string Label(int n) =>
                n == styleNumber && styleDiameter > 0 ? F("{0} (Ø {1} from the style)", n, styleDiameter)
                : CamLibraries.FindTool(doc, PartOf(first), n, first is TurningOp) is { } t ? CamLibraries.ToolLabel(t)
                : F("{0} (not in the tool libraries)", n);
            var names = string.Join(", ", group.Take(8).Select(c => c.Op.Name)) + (group.Count() > 8 ? " …" : "");
            var answer = await Dialogs.Choose(this, T("Tool"),
                F("The style of {0} sets tool {1}; {2} sets tool {3}. Which tool should they use?", names, styleNumber, source, other),
                styleDiameter > 0 ? F("Tool {0}", Label(styleNumber)) : F("Tool {0} (style)", Label(styleNumber)),
                F("Tool {0} ({1})", Label(other), source));
            if (answer is not { } a) continue;
            var chosen = a == 0 ? styleNumber : other;
            foreach (var c in group) c.Op.ToolNumber.Set(chosen);
            Log(F("{0}: tool {1} chosen.", names, chosen));
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A tool number was chosen but no tool library can give it (as on a computer without CamBam): offer to create the
    /// default library, once per session.
    /// </summary>
    private async Task AskForMissingToolLibrary(Part? part)
    {
        var doc = _session.Document;
        if (_defaultToolLibraryAsked || CamLibraries.HasToolLibrary(doc, part)) return;
        _defaultToolLibraryAsked = true;
        var name = CamLibraries.DefaultToolLibraryName(doc);
        var answer = await Dialogs.Choose(this, T("Tool Library"),
            F("There is no tool library: tool numbers are looked up in {0}, which does not exist yet. Create it now? Its tools can then be added in the System tab.", name),
            F("Create {0}", name), T("Later"));
        if (answer != 0) return;
        var lib = new ToolLibrary { Name = name };
        var path = Path.Combine(SystemLibrary.SystemFolder, "tools", name + ".xml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LibraryFiles.SaveTools(lib, path);
        }
        catch (Exception ex)
        {
            Log(F("Could not save tool library '{0}': {1}", name, ex.Message));
            return;
        }
        lib.SourcePath = path;
        CamLibraries.ToolLibraries.Add(lib);
        BuildSystemTree();
        Log(F("Tool library {0} created ({1}). Add its tools in the System tab: right-click the library, New Tool.", name, path));
    }
}
