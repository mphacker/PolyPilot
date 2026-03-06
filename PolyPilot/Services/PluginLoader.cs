using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using PolyPilot.Models;
using PolyPilot.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Services;

/// <summary>
/// Discovers and loads provider plugins from ~/.polypilot/plugins/.
/// Plugins are never auto-loaded — the user must explicitly approve each one in Settings → Plugins.
/// A SHA-256 hash check prevents silent DLL replacement.
/// </summary>
public static class PluginLoader
{
    private static string? _pluginsDir;
    private static string PluginsDir => _pluginsDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".polypilot", "plugins");

    /// <summary>
    /// Scans the plugins directory for DLLs. Returns metadata only — no assemblies are loaded.
    /// </summary>
    public static List<DiscoveredPlugin> DiscoverPlugins()
    {
        var plugins = new List<DiscoveredPlugin>();
        if (!Directory.Exists(PluginsDir))
            return plugins;

        foreach (var dll in Directory.EnumerateFiles(PluginsDir, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                var relativePath = Path.GetRelativePath(PluginsDir, dll);
                var fileInfo = new FileInfo(dll);
                var hash = ComputeHash(dll);

                plugins.Add(new DiscoveredPlugin
                {
                    Path = relativePath,
                    FullPath = dll,
                    Hash = hash,
                    FileName = Path.GetFileName(dll),
                    DirectoryName = Path.GetDirectoryName(relativePath) ?? "",
                    SizeBytes = fileInfo.Length
                });
            }
            catch
            {
                // Skip files we can't read
            }
        }

        return plugins;
    }

    /// <summary>
    /// Loads only user-approved plugins whose SHA-256 hash matches what was approved.
    /// Called during app startup, before builder.Build().
    /// </summary>
    public static List<string> LoadEnabledProviders(IServiceCollection services, IReadOnlyList<EnabledPlugin> enabledPlugins)
    {
        var warnings = new List<string>();

        foreach (var plugin in enabledPlugins)
        {
            var fullPath = Path.Combine(PluginsDir, plugin.Path);

            if (!File.Exists(fullPath))
            {
                warnings.Add($"Plugin '{plugin.DisplayName}' not found: {plugin.Path}");
                continue;
            }

            var currentHash = ComputeHash(fullPath);
            if (!string.Equals(currentHash, plugin.Hash, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Plugin '{plugin.DisplayName}' hash changed — needs re-approval");
                continue;
            }

            try
            {
                var loadContext = new PluginLoadContext(fullPath);
                var assembly = loadContext.LoadFromAssemblyPath(fullPath);
                var pluginDir = Path.GetDirectoryName(fullPath) ?? PluginsDir;

                foreach (var type in assembly.GetExportedTypes()
                    .Where(t => typeof(ISessionProviderFactory).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    if (Activator.CreateInstance(type) is ISessionProviderFactory factory)
                    {
                        factory.ConfigureServices(services, pluginDir);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Plugin '{plugin.DisplayName}' failed to load: {ex.Message}");
            }
        }

        return warnings;
    }

    internal static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Custom AssemblyLoadContext that shares host assemblies but isolates plugin-specific deps.
    /// </summary>
    private class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDir;

        // Assemblies shared with the host to avoid type identity conflicts
        private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "PolyPilot.Provider.Abstractions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.DependencyInjection",
            "GitHub.Copilot.SDK",
        };

        public PluginLoadContext(string pluginPath) : base(isCollectible: false)
        {
            _pluginDir = Path.GetDirectoryName(pluginPath) ?? "";
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share host assemblies to prevent type identity conflicts
            if (assemblyName.Name != null && SharedAssemblies.Contains(assemblyName.Name))
                return null; // Fall back to default context

            // Try to load from the plugin's directory
            if (assemblyName.Name != null)
            {
                var candidatePath = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
                if (File.Exists(candidatePath))
                    return LoadFromAssemblyPath(candidatePath);
            }

            return null;
        }
    }
}

/// <summary>
/// Metadata about a discovered plugin DLL. No code is loaded at this stage.
/// </summary>
public class DiscoveredPlugin
{
    public string Path { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Hash { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DirectoryName { get; init; } = "";
    public long SizeBytes { get; init; }
}
