using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Reko.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Reko;

/// <summary>
/// The Reko plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Reko";

    /// <inheritdoc />
    public override string Description =>
        "A Netflix-style discovery tab for Jellyfin, backed by TMDB with requests via Overseerr/Jellyseerr.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b3f19d2c-7a41-4e58-9c06-1d5a8e3f2b47");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "rekoconfig",
                DisplayName = "Reko",
                EnableInMainMenu = true,
                MenuSection = "General",
                MenuIcon = "movie",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "rekoconfigjs",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.js",
                    GetType().Namespace)
            }
        ];
    }
}
