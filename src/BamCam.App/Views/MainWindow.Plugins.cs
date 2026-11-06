using System.Reflection;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private readonly PluginManager _plugins = new();

    private static Version HostVersion => typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Finds plugins, asks once per new or changed file whether it may be loaded, loads the enabled ones and adds
    /// their commands and operations to the menus.
    /// </summary>
    private async Task LoadPluginsAsync()
    {
        _plugins.Discover(PluginManager.Folders, _settings.Plugins);
        foreach (var plugin in _plugins.Plugins.Where(p => p.State == PluginState.Duplicate))
            Log(F("Warning: {0}", F("Plugin {0} is installed twice. Using {1}, ignoring {2}.", plugin.Name, plugin.DuplicateOf ?? "", plugin.Path)));
        var changed = false;
        foreach (var plugin in _plugins.Plugins.Where(p => p.State == PluginState.NotApproved).ToList())
        {
            // Screenshot/diagnostic runs never ask.
            if (_screenshotMode) continue;
            var answer = await Dialogs.Ask(this, T("Plugin"),
                F("Load the plugin \"{0}\" by {1} (version {2})?\n\n{3}\n\nPlugins run with the same rights as BamCam. Only load plugins from vendors you trust.",
                    plugin.Name, plugin.Vendor, plugin.Version, plugin.Path), withCancel: false);
            _settings.Plugins.RemoveAll(a => string.Equals(a.File, plugin.Path, StringComparison.OrdinalIgnoreCase));
            _settings.Plugins.Add(new PluginApproval { File = plugin.Path, Hash = plugin.Hash, Enabled = answer == true });
            plugin.State = PluginState.Disabled;
            changed = true;
        }
        if (changed) SaveSettings();

        foreach (var plugin in _plugins.Plugins.Where(p => PluginManager.IsEnabled(p, _settings.Plugins)))
        {
            _plugins.Load(plugin, HostVersion, Log);
            Log(plugin.State == PluginState.Loaded
                ? F("Plugin loaded: {0} {1} ({2})", plugin.Name, plugin.Version, plugin.Vendor)
                : F("Plugin {0} could not be loaded: {1}", plugin.Name, plugin.Error ?? ""));
        }
        if (_plugins.Commands.Count > 0 || _plugins.Operations.Count > 0 || _plugins.Exporters.Count > 0)
        {
            BuildMenu();
            // Plugin commands with an icon get toolbar buttons.
            if (_plugins.Commands.Any(c => !string.IsNullOrWhiteSpace(c.Command.Icon))) BuildToolbar();
        }
    }

    /// <summary>Menu items of plugin commands for a top-level menu ("Plugins", "Draw", "Edit", "Machining").</summary>
    private object[] PluginMenuItems(string menu)
    {
        var items = _plugins.Commands
            .Where(c => string.Equals(c.Command.Menu, menu, StringComparison.OrdinalIgnoreCase) ||
                        menu == "Plugins" && c.Command.Menu is not ("Draw" or "Edit" or "Machining"))
            .Select(c =>
            {
                var item = Item(c.Command.Header, () => _ = RunPluginCommand(c));
                if (c.Command.Description is { } tip) Avalonia.Controls.ToolTip.SetTip(item, tip);
                return (object)item;
            })
            .ToList();
        if (items.Count > 0 && menu != "Plugins") items.Insert(0, "-");
        return items.ToArray();
    }

    /// <summary>Machining menu entries of operation types added by plugins.</summary>
    private object[] PluginOperationItems()
    {
        var items = _plugins.Operations.Select(o => (object)Item(T(o.DisplayType), () => CreateOp(o.Create()))).ToList();
        if (items.Count > 0) items.Insert(0, "-");
        return items.ToArray();
    }

    private async Task RunPluginCommand(PluginCommandEntry entry)
    {
        if (entry.Command.RequiresSelection && Selected.Count == 0)
        {
            Log(T("Select drawing objects first."));
            return;
        }
        try
        {
            await entry.Command.Execute(new CommandContext(this));
        }
        catch (Exception ex)
        {
            Log(F("Plugin command '{0}' failed: {1}", entry.Command.Header.Replace("_", ""), (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message));
        }
        View.Invalidate();
    }

    /// <summary>What a plugin command may do: undoable changes, selection, messages and simple input dialogs.</summary>
    /// <remarks>With a group (one run of a script) all changes are one undo step.</remarks>
    private sealed class CommandContext(MainWindow window, DocumentSession.UndoGroup? group = null) : ICommandContext
    {
        public CadDocument Document => window._session.Document;
        public IReadOnlyList<Entity> Selection => window.Selected;

        public void Change(string description, Action change)
        {
            if (group != null) window._session.Execute(group, change);
            else window._session.Execute(description, change);
        }

        public void AddEntities(string description, IEnumerable<Entity> entities)
        {
            var list = entities.ToList();
            Change(description, () =>
            {
                foreach (var e in list) window._session.Document.AddEntity(e);
            });
            window._session.Select(list);
        }

        public void Select(IEnumerable<Entity> entities) => window._session.Select(entities);
        public void Log(string message) => window.Log(message);
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Dialogs.PromptNumber(window, title, label, defaultValue);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Dialogs.Prompt(window, title, label, defaultValue);
        public Task<string?> PostGCode() => window.PostGCodeText();
        public void StartTool(PluginTool tool) => window.View.SetTool(new PluginToolAdapter(window, tool));
    }

    /// <summary>
    /// Runs a plugin's <see cref="PluginTool"/> as a drawing tool: snapped mouse points, clicks, keys, Enter / middle
    /// click and the preview. Errors of the plugin end the tool with a message instead of breaking BamCam.
    /// </summary>
    private sealed class PluginToolAdapter(MainWindow window, PluginTool tool) : ViewTool, IToolContext
    {
        private readonly CommandContext _commands = new(window);
        private bool _ended;

        public override string Prompt => Safe(() => tool.Prompt) ?? "";
        public override bool WantsPlainKeys => true;

        public Point2 Cursor { get; private set; }
        public double PixelSize => Host.View.ToWorldLength(1);

        protected override void OnAttached() => Run(() => tool.Started(this));

        public override void OnDetached()
        {
            if (_ended) return;
            _ended = true;
            Run(() => tool.Ended(this));
        }

        public override void PointerMoved(Avalonia.Point screen, Point2 world, Avalonia.Input.KeyModifiers mods)
        {
            Cursor = world;
            Run(() => tool.Moved(this, world));
            Host.Invalidate();
        }

        public override void PointerPressed(Avalonia.Point screen, Point2 world, Avalonia.Input.PointerPointProperties props, Avalonia.Input.KeyModifiers mods)
        {
            if (!props.IsLeftButtonPressed) return;
            Cursor = world;
            var m = (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? ToolModifiers.Shift : 0) |
                    (mods.HasFlag(Avalonia.Input.KeyModifiers.Control) ? ToolModifiers.Control : 0) |
                    (mods.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? ToolModifiers.Alt : 0);
            Run(() => tool.Clicked(this, world, m));
            Host.Invalidate();
        }

        public override void PointerReleased(Avalonia.Point screen, Point2 world, Avalonia.Input.MouseButton button, Avalonia.Input.KeyModifiers mods)
        {
            if (button == Avalonia.Input.MouseButton.Middle) Run(() => tool.Finished(this));
        }

        public override bool KeyDown(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods)
        {
            if (key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)
            {
                Run(() => tool.Finished(this));
                return true;
            }
            if (KeyName(key) is not { } name) return false;
            var used = Safe(() => tool.KeyPressed(this, name));
            if (used != true && key == Avalonia.Input.Key.Escape)
            {
                End();
                return true;
            }
            Host.Invalidate();
            return used == true;
        }

        private static string? KeyName(Avalonia.Input.Key key) => key switch
        {
            >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => key.ToString(),
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => ((char)('0' + (key - Avalonia.Input.Key.D0))).ToString(),
            >= Avalonia.Input.Key.NumPad0 and <= Avalonia.Input.Key.NumPad9 => ((char)('0' + (key - Avalonia.Input.Key.NumPad0))).ToString(),
            Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete or Avalonia.Input.Key.Space or Avalonia.Input.Key.Tab or Avalonia.Input.Key.Escape
                or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right => key.ToString(),
            _ => null,
        };

        public override void RenderOverlay(Avalonia.Media.DrawingContext dc, ViewTransform view)
        {
            if (Safe(() => tool.Preview(this).ToList()) is not { } paths) return;
            foreach (var p in paths) DrawPath(dc, view, p, PreviewPen);
        }

        public IReadOnlyList<Entity> PickAt(Point2 point) => Host.HitTest(Host.View.ToScreen(point), 8);
        public void SetPrompt(string text) => Host.SetPrompt(text);
        public void Redraw() => Host.Invalidate();

        public void End()
        {
            if (_ended) return;
            Host.EndTool(); // detaches: Ended is called there
        }

        // Everything a command can do.
        public CadDocument Document => _commands.Document;
        public IReadOnlyList<Entity> Selection => _commands.Selection;
        public void Change(string description, Action change) => _commands.Change(description, change);
        public void AddEntities(string description, IEnumerable<Entity> entities) => _commands.AddEntities(description, entities);
        public void Select(IEnumerable<Entity> entities) => _commands.Select(entities);
        public void Log(string message) => _commands.Log(message);
        public Task<double?> AskNumber(string title, string label, double defaultValue) => _commands.AskNumber(title, label, defaultValue);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => _commands.AskText(title, label, defaultValue);
        public void StartTool(PluginTool next) => _commands.StartTool(next);

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                window.Log(F("Plugin tool failed: {0}", ex.Message));
                End();
            }
        }

        private T? Safe<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (Exception ex)
            {
                window.Log(F("Plugin tool failed: {0}", ex.Message));
                return default;
            }
        }
    }

    private async void ShowPluginManager()
    {
        var window = new PluginManagerWindow(_plugins, _settings.Plugins);
        await window.ShowDialog(this);
        if (window.Changed)
        {
            SaveSettings();
            Log(T("Plugin settings saved. Changes take effect after restarting BamCam."));
        }
    }
}
