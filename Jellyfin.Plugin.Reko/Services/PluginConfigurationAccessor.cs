using Jellyfin.Plugin.Reko.Configuration;

namespace Jellyfin.Plugin.Reko.Services;

/// <summary>
/// Reads the live plugin configuration.
/// </summary>
/// <remarks>
/// <c>BasePlugin&lt;T&gt;.Configuration</c> is a live object that Jellyfin replaces wholesale when an
/// administrator saves, so this wrapper is mostly a null guard: the plugin instance does not exist
/// during very early startup, and a service that dereferences it blindly would take the whole server
/// down with a null reference.
/// </remarks>
public sealed class PluginConfigurationAccessor
{
    /// <summary>
    /// Gets the current configuration, or a default instance when the plugin is not loaded.
    /// </summary>
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether the plugin instance is available.
    /// </summary>
    public bool IsPluginLoaded => Plugin.Instance is not null;
}
