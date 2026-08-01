using NUnit.Framework;

// No namespace on purpose: a [SetUpFixture] outside any namespace applies to the whole assembly, so this runs once
// before any fixture regardless of where new tests are added.

/// <summary>
/// Keeps this suite out of the developer's machine-local database registry (#622, design D-7).
/// </summary>
/// <remarks>
/// Unlike the other suites this one is <b>not</b> covered by the automatic temp-directory guard: AntHill databases are
/// built in <c>AppContext.BaseDirectory</c> (<c>TyphonBridge.cs</c>), which is the test binaries' own folder. The
/// opt-out therefore has to be explicit here. The opt-out is per <i>process</i>, so <c>AntHill.Demo</c> — a real
/// application whose shard is worth finding — keeps registering.
/// </remarks>
[SetUpFixture]
public class DatabaseRegistryOptOut
{
    [OneTimeSetUp]
    public void Disable() => Typhon.Engine.DatabaseRegistry.SuppressForProcess = true;
}
