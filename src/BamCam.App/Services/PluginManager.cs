using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using BamCam.Core.Cam;
using BamCam.Core.Localization;
using BamCam.Plugins;

namespace BamCam.App.Services;

public enum PluginState { NotApproved, Disabled, Loaded, Failed }

/// <summary>A plugin assembly found in a plugin folder.</summary>
public sealed class PluginInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public required string Version { get; init; }
    /// <summary>SHA-256 of the file: an approval is only valid for exactly this file.</summary>
    public required string Hash { get; init; }
    public PluginState State { get; set; } = PluginState.NotApproved;
    public string? Error { get; set; }
}

/// <summary>Approval of a plugin file, stored in the settings.</summary>
public sealed class PluginApproval
{
    public string File { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>A command registered by a plugin.</summary>
public sealed record PluginCommandEntry(PluginInfo Plugin, PluginCommand Command);

/// <summary>A machining operation type registered by a plugin.</summary>
public sealed record PluginOperationEntry(PluginInfo Plugin, string XmlName, string DisplayType, Func<MachineOp> Create);

/// <summary>
/// Finds, approves and loads plugins (.NET assemblies referencing BamCam.PluginApi) from the plugin folders.
/// Discovery only reads metadata; code runs only after the user approved the exact file.
/// </summary>
public sealed class PluginManager
{
    public const string ApiAssembly = "BamCam.PluginApi";

    public List<PluginInfo> Plugins { get; } = [];
    public List<PluginCommandEntry> Commands { get; } = [];
    public List<PluginOperationEntry> Operations { get; } = [];

    /// <summary>The plugin folder in the user's system folder.</summary>
    public static string UserFolder => System.IO.Path.Combine(SystemLibrary.SystemFolder, "plugins");

    /// <summary>Folders searched for plugins: next to the program (bundled) and in the system folder.</summary>
    public static IEnumerable<string> Folders => [System.IO.Path.Combine(AppContext.BaseDirectory, "plugins"), UserFolder];

    /// <summary>Lists plugin assemblies (without running them) and applies the stored approvals.</summary>
    public void Discover(IEnumerable<string> folders, IReadOnlyList<PluginApproval> approvals)
    {
        Plugins.Clear();
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var file in Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories).Order())
        {
            if (Inspect(file) is not { } info) continue;
            var approval = approvals.FirstOrDefault(a => string.Equals(a.File, info.Path, StringComparison.OrdinalIgnoreCase));
            // Approved files stay "Disabled" until they are loaded.
            info.State = approval == null || approval.Hash != info.Hash ? PluginState.NotApproved : PluginState.Disabled;
            Plugins.Add(info);
        }
    }

    /// <summary>True when the stored approval enables exactly this file.</summary>
    public static bool IsEnabled(PluginInfo p, IReadOnlyList<PluginApproval> approvals) =>
        approvals.Any(a => string.Equals(a.File, p.Path, StringComparison.OrdinalIgnoreCase) && a.Hash == p.Hash && a.Enabled);

    /// <summary>
    /// Plugin information from the file's metadata, or null when it is no plugin (does not reference the plugin API).
    /// Nothing of the file is executed.
    /// </summary>
    public static PluginInfo? Inspect(string file)
    {
        try
        {
            using (var stream = File.OpenRead(file))
            using (var pe = new PEReader(stream))
            {
                if (!pe.HasMetadata) return null;
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) return null;
                if (md.GetString(md.GetAssemblyDefinition().Name) == ApiAssembly) return null;
                var referencesApi = md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == ApiAssembly);
                if (!referencesApi) return null;
            }
            var version = FileVersionInfo.GetVersionInfo(file);
            var name = FirstNonEmpty(version.ProductName, version.FileDescription, System.IO.Path.GetFileNameWithoutExtension(file));
            return new PluginInfo
            {
                Path = System.IO.Path.GetFullPath(file),
                Name = name,
                Vendor = FirstNonEmpty(version.CompanyName, Tr.T("unknown vendor")),
                Version = FirstNonEmpty(version.ProductVersion?.Split('+')[0], version.FileVersion, "?"),
                Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            };
        }
        catch (BadImageFormatException)
        {
            return null; // native DLL
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    /// <summary>Loads an approved plugin and lets it register its commands and operations.</summary>
    public void Load(PluginInfo plugin, Version hostVersion, Action<string> log)
    {
        try
        {
            var context = new PluginLoadContext(plugin.Path);
            var assembly = context.LoadFromAssemblyPath(plugin.Path);
            var entryTypes = assembly.GetExportedTypes()
                .Where(t => typeof(IBamCamPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();
            if (entryTypes.Count == 0) throw new InvalidOperationException(Tr.T("The assembly contains no public IBamCamPlugin class."));
            var host = new Host(this, plugin, hostVersion, log);
            foreach (var type in entryTypes) ((IBamCamPlugin)Activator.CreateInstance(type)!).Initialize(host);
            plugin.State = PluginState.Loaded;
            plugin.Error = null;
        }
        catch (Exception ex)
        {
            plugin.State = PluginState.Failed;
            plugin.Error = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
        }
    }

    private sealed class Host(PluginManager manager, PluginInfo plugin, Version hostVersion, Action<string> log) : IPluginHost
    {
        public Version HostVersion => hostVersion;
        public string Language => Tr.Language;

        public void AddCommand(PluginCommand command) => manager.Commands.Add(new PluginCommandEntry(plugin, command));

        public void AddOperation<TOp>(Action<ToolpathContext, TOp, Toolpath> generator) where TOp : MachineOp, new()
        {
            var sample = new TOp();
            MachineOpFactory.Register(sample.XmlName, () => new TOp());
            ToolpathGenerator.RegisterGenerator(generator);
            manager.Operations.RemoveAll(o => o.XmlName == sample.XmlName);
            manager.Operations.Add(new PluginOperationEntry(plugin, sample.XmlName, sample.DisplayType, () => new TOp()));
        }

        public void Log(string message) => log($"{plugin.Name}: {message}");
    }

    /// <summary>
    /// Loads a plugin with its own dependencies; BamCam's own assemblies (and everything the application already
    /// loaded) are shared so that plugin types derive from the same MachineOp, Entity, ... as the application.
    /// </summary>
    private sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(System.IO.Path.GetFileNameWithoutExtension(mainAssembly))
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);
        private readonly string _folder = System.IO.Path.GetDirectoryName(mainAssembly)!;

        protected override Assembly? Load(AssemblyName name)
        {
            // BamCam's own assemblies and everything the application already uses: always the application's copy,
            // whatever version the plugin was built against (an installed BamCam may carry another version).
            if (name.Name is { } n && (n.StartsWith("BamCam.", StringComparison.Ordinal) || Default.Assemblies.Any(a => a.GetName().Name == n)))
                return Default.Assemblies.FirstOrDefault(a => a.GetName().Name == n) ?? Default.LoadFromAssemblyName(new AssemblyName(n));
            var path = _resolver.ResolveAssemblyToPath(name) ?? System.IO.Path.Combine(_folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
