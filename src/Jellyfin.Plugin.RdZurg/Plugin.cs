using System;
using System.Collections.Generic;
using Jellyfin.Plugin.RdZurg.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RdZurg;

/// <summary>
/// Serves a Real-Debrid account as a Jellyfin library, with no mount and no second service.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin's application paths.</param>
    /// <param name="xmlSerializer">The serializer used for the configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataPath = applicationPaths.DataPath;
        // Persist the generated signing key on first install and when upgrading old configurations.
        SaveConfiguration();
    }

    /// <summary>Gets Jellyfin's data directory, where the plugin keeps its library anchors.</summary>
    public string DataPath { get; } = string.Empty;

    /// <summary>Gets the running instance, for the parts of Jellyfin that hand out no reference.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "RD zurg";

    /// <inheritdoc />
    public override string Description => "Your Real-Debrid library in Jellyfin, without a mount.";

    /// <inheritdoc />
    public override Guid Id => new("4d0b1a37-1f1c-4a3e-9f5c-2e6a7b8c9d01");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var config = (PluginConfiguration)configuration;
        config.Validate();
        base.UpdateConfiguration(config);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
