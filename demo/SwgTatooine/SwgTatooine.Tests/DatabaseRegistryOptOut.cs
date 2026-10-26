using NUnit.Framework;

// No namespace on purpose: a [SetUpFixture] outside any namespace applies to the whole assembly, so this runs once
// before any fixture regardless of where new tests are added.

/// <summary>
/// Keeps this suite out of the developer's machine-local database registry (#622, design D-7).
/// </summary>
/// <remarks>
/// The fixtures here already build their databases under <c>%TEMP%</c>, which the registry suppresses on its own. This
/// is the explicit half of that guard, so a future fixture that builds a shard elsewhere cannot silently start writing
/// rows into a real <c>%LOCALAPPDATA%</c>.
/// </remarks>
[SetUpFixture]
public class DatabaseRegistryOptOut
{
    [OneTimeSetUp]
    public void Disable() => Typhon.Engine.DatabaseRegistry.SuppressForProcess = true;
}
