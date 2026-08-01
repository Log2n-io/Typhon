using NUnit.Framework;

// No namespace on purpose: a [SetUpFixture] outside any namespace applies to the whole assembly, so this runs once
// before any fixture regardless of where new tests are added.

/// <summary>
/// Keeps this suite out of the developer's machine-local database registry (#622, design D-7).
/// </summary>
/// <remarks>
/// Every fixture here already builds its databases under <c>%TEMP%</c>, which the registry suppresses on its own. This
/// is the explicit half of that guard: a future fixture that legitimately builds a database elsewhere must not silently
/// start writing rows into a real <c>%LOCALAPPDATA%</c>, and nobody should have to remember that rule.
/// </remarks>
[SetUpFixture]
public class DatabaseRegistryOptOut
{
    [OneTimeSetUp]
    public void Disable() => DatabaseRegistry.SuppressForProcess = true;
}
