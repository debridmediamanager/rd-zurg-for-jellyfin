using Xunit;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Tests that install their own <see cref="Plugin.Instance"/> or <c>BaseItem.LibraryManager</c>.
/// </summary>
/// <remarks>
/// Both are process-wide statics, so two such tests running at once would sync against each other's
/// configuration and library.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceCollection
{
    public const string Name = "Plugin instance";
}
