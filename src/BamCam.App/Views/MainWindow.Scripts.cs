using BamCam.App.Services;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>Connects script objects to the compiler and to the user's approvals (called once at start).</summary>
    private void InitScripts()
    {
        ScriptEntity.Runner = ScriptRunner.Run;
        ScriptEntity.IsAllowed = hash => _settings.ApprovedScripts.Contains(hash) || _triedScripts.Contains(hash);
    }

    /// <summary>Script texts run while editing (allowed for this session only; the final one is saved).</summary>
    private readonly HashSet<string> _triedScripts = [];

    /// <summary>Remembers that the user allows this script text (written or confirmed by the user).</summary>
    private void AllowScript(string script)
    {
        var hash = ScriptEntity.Hash(script);
        if (_settings.ApprovedScripts.Contains(hash)) return;
        _settings.ApprovedScripts.Add(hash);
        SaveSettings();
    }

    /// <summary>
    /// After opening a drawing: scripts not allowed yet are asked about once; until then their saved outlines show.
    /// </summary>
    private async Task AskForScripts(CadDocument doc)
    {
        var pending = doc.AllEntities().OfType<ScriptEntity>().Where(s => !ScriptEntity.IsAllowed(ScriptEntity.Hash(s.Script))).ToList();
        if (pending.Count == 0 || _screenshotMode) return;
        var answer = await Dialogs.Ask(this, T("Script Objects"),
            F("This drawing contains {0} script object(s) with C# code. Scripts can do anything a program can do on this computer.\n\nRun them? (No = show the outlines saved in the drawing.)", pending.Count),
            withCancel: false);
        if (answer != true)
        {
            Log(F("{0} script object(s) not run: the saved outlines are shown.", pending.Count));
            return;
        }
        foreach (var s in pending) AllowScript(s.Script);
        foreach (var s in pending) s.Invalidate();
        ReportScriptErrors(pending);
        _session.NotifyChanged();
    }

    private void ReportScriptErrors(IEnumerable<ScriptEntity> scripts)
    {
        foreach (var s in scripts)
            if (!s.IsComputed) Log(F("Script object {0}: {1}", s.Id, T(s.Status)));
    }

    /// <summary>Draw ▸ Script Object: a new script object with an example script, then the editor.</summary>
    private async void NewScriptObject()
    {
        var script = new ScriptEntity();
        AllowScript(script.Script);
        _session.Execute(T("Insert script object"), () => _session.Document.AddEntity(script));
        _session.Select([script]);
        await EditScript(script);
        View.ZoomToFit();
    }

    /// <summary>Edit ▸ Edit Script for the selected script object.</summary>
    private async void EditSelectedScript()
    {
        if (Selected.Count != 1 || Selected[0] is not ScriptEntity script)
        {
            Log(T("Select one script object."));
            return;
        }
        await EditScript(script);
    }

    private async Task EditScript(ScriptEntity script)
    {
        var original = script.Script;
        // Run shows the result in the drawing at once (without an undo step); OK makes it one step, Cancel undoes it.
        (int? Outlines, string? Error) Try(string text)
        {
            _triedScripts.Add(ScriptEntity.Hash(text)); // written by the user
            script.Script = text;
            script.Invalidate();
            View.Invalidate();
            return script.IsComputed ? (script.ToPaths().Count(), null) : (null, script.Status);
        }
        var editor = new ScriptEditorWindow(original, Try);
        await editor.ShowDialog(this);
        script.Script = original;
        script.Invalidate();
        var text = editor.Result;
        if (text == null || text == original)
        {
            View.Invalidate();
            return;
        }
        AllowScript(text);
        _session.Execute(T("Edit script"), () =>
        {
            script.Script = text;
            script.Invalidate();
        });
        Log(script.IsComputed ? F("Script object {0}: {1} outlines.", script.Id, script.ToPaths().Count()) : F("Script object {0}: {1}", script.Id, T(script.Status)));
        _session.Inspect(script);
    }
}
