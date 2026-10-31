using System.Reflection;
using BamCam.App.Services;
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
        if (_plugins.Commands.Count > 0 || _plugins.Operations.Count > 0) BuildMenu();
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
    private sealed class CommandContext(MainWindow window) : ICommandContext
    {
        public CadDocument Document => window._session.Document;
        public IReadOnlyList<Entity> Selection => window.Selected;

        public void Change(string description, Action change) => window._session.Execute(description, change);

        public void AddEntities(string description, IEnumerable<Entity> entities)
        {
            var list = entities.ToList();
            window._session.Execute(description, () =>
            {
                foreach (var e in list) window._session.Document.AddEntity(e);
            });
            window._session.Select(list);
        }

        public void Select(IEnumerable<Entity> entities) => window._session.Select(entities);
        public void Log(string message) => window.Log(message);
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Dialogs.PromptNumber(window, title, label, defaultValue);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Dialogs.Prompt(window, title, label, defaultValue);
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
