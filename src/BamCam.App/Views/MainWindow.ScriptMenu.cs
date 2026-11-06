using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BamCam.App.Services;
using BamCam.Core.Model;
using BamCam.Plugins;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private MenuItem? _scriptMenu;
    private CancellationTokenSource? _runningScript;

    /// <summary>The Scripts menu; its list is read from the folder each time it opens.</summary>
    private MenuItem ScriptMenu()
    {
        _scriptMenu = new MenuItem { Header = T("_Scripts") };
        _scriptMenu.SubmenuOpened += (sender, e) =>
        {
            if (e.Source == _scriptMenu) FillScriptMenu();
        };
        FillScriptMenu();
        return _scriptMenu;
    }

    private void FillScriptMenu()
    {
        if (_scriptMenu == null) return;
        try
        {
            ScriptLibrary.EnsureExamples(ScriptLibrary.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(ex.Message);
        }
        var scripts = ScriptLibrary.List(ScriptLibrary.Folder);
        _scriptMenu.Items.Clear();
        _scriptMenu.Items.Add(Item("_New Script...", () => EditScriptFile(null)));
        var edit = Sub("_Edit Script");
        edit.IsEnabled = scripts.Count > 0;
        foreach (var item in ScriptItems(scripts, EditScriptFile)) edit.Items.Add(item);
        _scriptMenu.Items.Add(edit);
        _scriptMenu.Items.Add(Item("_Add Script File...", AddScriptFile));
        _scriptMenu.Items.Add(Item("Open Script _Folder", () => OpenFolder(ScriptLibrary.Folder)));
        var stop = Item("S_top Running Script", StopScript);
        stop.IsEnabled = _runningScript != null;
        _scriptMenu.Items.Add(stop);
        _scriptMenu.Items.Add(new Separator());
        if (scripts.Count == 0) _scriptMenu.Items.Add(new MenuItem { Header = T("(empty)"), IsEnabled = false });
        foreach (var item in ScriptItems(scripts, path => _ = RunScriptFile(path))) _scriptMenu.Items.Add(item);
    }

    /// <summary>Menu items of the scripts; a folder is a submenu.</summary>
    private static IEnumerable<MenuItem> ScriptItems(IEnumerable<ScriptEntry> entries, Action<string> action)
    {
        foreach (var entry in entries)
        {
            // The name is shown as it is: a single underscore would mark an access key.
            var item = new MenuItem { Header = entry.Name.Replace("_", "__") };
            if (entry.Children != null)
                foreach (var child in ScriptItems(entry.Children, action)) item.Items.Add(child);
            else
                item.Click += (_, _) => action(entry.Path);
            yield return item;
        }
    }

    /// <summary>Scripts ▸ New / Edit: the editor stays open beside the drawing.</summary>
    private void EditScriptFile(string? path)
    {
        string text;
        try
        {
            text = path == null ? ScriptLibrary.NewScript : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(F("Error opening {0}: {1}", path ?? "", ex.Message));
            return;
        }
        var actions = new ScriptFileActions(
            RunScriptText, // in the editor the user sees what runs
            AllowScript);
        new ScriptEditorWindow(text, path, actions).Show(this);
    }

    /// <summary>Scripts ▸ Add Script File: copies a file into the folder. Nothing of it runs.</summary>
    private async void AddScriptFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Add script file"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(T("C# script")) { Patterns = ["*" + ScriptLibrary.Extension] }, AllType],
        });
        foreach (var file in files)
            if (file.TryGetLocalPath() is { } path) await AddScriptFile(path);
    }

    /// <summary>Copies a script file into the folder; it appears in the Scripts menu and asks before its first run.</summary>
    private async Task AddScriptFile(string path)
    {
        try
        {
            if (!ScriptLibrary.Add(path, ScriptLibrary.Folder, overwrite: false, out var target))
            {
                if (await Dialogs.Ask(this, T("Add script file"), F("Replace the existing script {0}?", Path.GetFileName(target)), withCancel: false) != true) return;
                ScriptLibrary.Add(path, ScriptLibrary.Folder, overwrite: true, out target);
            }
            Log(F("Script added to the Scripts menu: {0}", Path.GetFileNameWithoutExtension(target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(F("Error opening {0}: {1}", path, ex.Message));
        }
    }

    private async Task RunScriptFile(string path)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(F("Error opening {0}: {1}", path, ex.Message));
            return;
        }
        var name = Path.GetFileNameWithoutExtension(path);
        if (!ScriptEntity.IsAllowed(ScriptEntity.Hash(text)))
        {
            var approval = new ScriptApprovalWindow(name, path, text);
            await approval.ShowDialog(this);
            if (!approval.Approved)
            {
                Log(F("Script {0} not run.", name));
                return;
            }
            AllowScript(text);
        }
        await RunScriptText(text, name);
    }

    /// <summary>Runs a script of the Scripts menu as one undo step; returns an error text or null.</summary>
    private async Task<string?> RunScriptText(string text, string name)
    {
        if (_runningScript != null)
        {
            var busy = T("Another script is still running. Stop it with Scripts > Stop Running Script.");
            Log(busy);
            return busy;
        }
        using var stop = new CancellationTokenSource();
        _runningScript = stop;
        var commands = new CommandContext(this, new DocumentSession.UndoGroup());
        var context = new CommandScriptContext(commands, OnUserInterface, stop.Token);
        Log(F("Script {0}: running.", name));
        // A long script is not locked in: tell where to stop it.
        _ = Task.Delay(TimeSpan.FromSeconds(10), stop.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && _runningScript == stop)
                Dispatcher.UIThread.Post(() => Log(F("Script {0} is still running. Scripts > Stop Running Script ends it.", name)));
        }, TaskScheduler.Default);
        try
        {
            var result = await ScriptRunner.RunMenuScript(text, context, stop.Token);
            if (result.Stopped)
            {
                Log(F("Script {0}: stopped. It can no longer change the drawing; a loop that does not check Cancel computes on until BamCam is closed.", name));
                return T("Script stopped.");
            }
            if (result.Error != null)
            {
                Log(F("Script {0}: {1}", name, result.Error));
                return result.Error;
            }
            if (context.Paths.Count > 0)
                commands.AddEntities(F("Script {0}", name), context.Paths.Select(p => new PolylineEntity(p)));
            Log(F("Script {0}: finished, {1} outlines added.", name, context.Paths.Count));
            return null;
        }
        finally
        {
            _runningScript = null;
            View.Invalidate();
        }
    }

    private void StopScript() => _runningScript?.Cancel();

    private static Task OnUserInterface(Func<Task> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
}
