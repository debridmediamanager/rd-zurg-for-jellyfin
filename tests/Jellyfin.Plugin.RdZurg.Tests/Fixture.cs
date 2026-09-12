using System;
using System.IO;

namespace Jellyfin.Plugin.RdZurg.Tests;

/// <summary>
/// Loads captured real data the tests run against.
/// </summary>
/// <remarks>
/// The traps these tests exist for are in what a real library and a real account actually hold -
/// an item filed as a version of a copy of itself, a film whose links were erased - and a
/// hand-written approximation would encode what the author expected instead.
/// </remarks>
internal static class Fixture
{
    public static string Read(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
