using System.IO;
using NUnit.Framework;
using SwgTatooine.Tests;

// No namespace on purpose: a [SetUpFixture] outside any namespace applies to the whole assembly, so this runs once after every fixture of the run.

/// <summary>
/// Removes this process's world root (<see cref="Worlds.Root"/>) at the end of the run, and reports what was still in it.
/// </summary>
/// <remarks>
/// <b>The net under the per-case teardowns, not a replacement for them.</b> Three fixtures had no <c>[TearDown]</c> and one case
/// built a second world it never deleted: 17 directories per suite run, 768 of them and 12 GB in <c>%TEMP%</c> by the time anyone
/// looked. A directory still here is a case that forgot to clean up, so it is counted on the console before it goes.
/// </remarks>
[SetUpFixture]
public class TempWorldsCleanup
{
    [OneTimeTearDown]
    public void RemoveTheRoot()
    {
        if (!Directory.Exists(Worlds.Root))
        {
            return;
        }

        var leaked = Directory.GetDirectories(Worlds.Root).Length;
        if (leaked > 0)
        {
            TestContext.Progress.WriteLine($"{leaked} world director{(leaked == 1 ? "y" : "ies")} outlived the case that made it; removing {Worlds.Root}");
        }

        Worlds.Delete(Worlds.Root);
    }
}
