using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>The realm frames the client suites decode positions over (<c>typhon.3</c>).</summary>
internal static class TestFrames
{
    /// <summary>catalog-kitchen-sink's realm, the same numbers as the protocol suite's: deep, ±8192 m by ±64 m at 24 bits, 256 m cells.</summary>
    internal static RealmFrame Kitchen => new(3, 7, 1, 0xC0FFEE, 24, 256, deep: true, [-8192, -8192, -64], [8192, 8192, 64]);

    /// <summary>A store that already holds <see cref="Kitchen"/>, as if a <c>REALM</c> block had placed its session: for tests of frames that follow one.</summary>
    internal static WorldStore InKitchen(WorldStore store)
    {
        store.SetRealm(Kitchen);
        return store;
    }
}
