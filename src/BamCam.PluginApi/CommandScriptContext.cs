using BamCam.Core.Model;

namespace BamCam.Plugins;

/// <summary>
/// What a script of the Scripts menu can use: everything a script object can (<see cref="ScriptContext.Add(BamCam.Core.Geometry.Path2)"/>
/// and the helpers; added outlines become polylines when the script ends), and the drawing, the selection, undoable
/// changes, messages and input dialogs like a plugin command. The script runs on a background thread, so that
/// a long one does not block BamCam; these members carry the work over to the user interface.
/// </summary>
public class CommandScriptContext(ICommandContext commands, Func<Func<Task>, Task> onUserInterface, CancellationToken cancel) : ScriptContext
{
    /// <summary>The open drawing. Change it inside <see cref="Change"/> only.</summary>
    public CadDocument Document => commands.Document;

    /// <summary>Set when the user stops the script: a long loop should end when this is cancelled.</summary>
    public CancellationToken Cancel => cancel;

    /// <summary>The selected drawing objects at this moment.</summary>
    public IReadOnlyList<Entity> Selection
    {
        get
        {
            IReadOnlyList<Entity> selection = [];
            Do(() => selection = commands.Selection.ToList());
            return selection;
        }
    }

    /// <summary>Runs a change of the drawing. All changes of one run of a script are one undo step.</summary>
    public void Change(string description, Action change) => Do(() => commands.Change(description, change));

    /// <summary>Adds objects to the active layer and selects them.</summary>
    public void AddEntities(string description, IEnumerable<Entity> entities)
    {
        var list = entities.ToList();
        Do(() => commands.AddEntities(description, list));
    }

    /// <summary>Replaces the selection.</summary>
    public void Select(IEnumerable<Entity> entities)
    {
        var list = entities.ToList();
        Do(() => commands.Select(list));
    }

    /// <summary>Writes a line to the message window.</summary>
    public void Log(string message) => Do(() => commands.Log(message));

    /// <summary>Asks for a number; null when cancelled.</summary>
    public async Task<double?> AskNumber(string title, string label, double defaultValue)
    {
        cancel.ThrowIfCancellationRequested();
        double? answer = null;
        await onUserInterface(async () => answer = await commands.AskNumber(title, label, defaultValue));
        return answer;
    }

    /// <summary>Asks for a text; null when cancelled.</summary>
    public async Task<string?> AskText(string title, string label, string defaultValue = "")
    {
        cancel.ThrowIfCancellationRequested();
        string? answer = null;
        await onUserInterface(async () => answer = await commands.AskText(title, label, defaultValue));
        return answer;
    }

    /// <summary>A stopped script can no longer touch the drawing, even when it does not look at <see cref="Cancel"/>.</summary>
    private void Do(Action action)
    {
        cancel.ThrowIfCancellationRequested();
        onUserInterface(() =>
        {
            action();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }
}
