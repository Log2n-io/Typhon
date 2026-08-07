using System;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Typhon.Engine.Tests;

/// <summary>
/// The suite's seed source: deterministic by default, randomised only where a run explicitly asks for it, and always replayable from one printed number
/// (#704 T6).
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> Every seed in the suite was a literal — <c>new Random(42)</c> ×8, <c>new Random(1234)</c>, <c>LifecycleChurnWorkload(1234, 24)</c>.
/// CI run #1 and CI run #1,000 therefore explored the identical state. The coverage surface of every comparable engine grows with CI-hours; ours was fixed on
/// the day each test was written.
/// </para>
/// <para>
/// <b>The PR gate stays deterministic.</b> With <c>TYPHON_TEST_SEED</c> unset, <see cref="RunSeed"/> is a constant, so a local run and a gate run behave
/// exactly as they did before. #703 spent real effort making green mean green; a gate that fails on a different cell each night would spend that back. Only
/// the nightly sets the variable — see <c>nightly-suppressed.yml</c>.
/// </para>
/// <para>
/// <b>One number reproduces the whole run.</b> Per-test seeds are DERIVED from the run seed and the test's own name rather than drawn independently, so a
/// failure needs no per-test bookkeeping: re-run with the printed <c>TYPHON_TEST_SEED</c> and every test — including the one that failed — draws the same
/// values again.
/// </para>
/// <para>
/// <b>Why the derivation is hand-written.</b> <c>string.GetHashCode()</c> cannot be used. Per the .NET documentation it is randomised per process: *"two
/// subsequent runs of the same program may return different hash codes"*. A seed derived from it would print one number and replay a different state, which
/// is worse than no seed at all. FNV-1a below is a stable function of its bytes on every platform and every run.
/// </para>
/// </remarks>
public static class TestSeed
{
    /// <summary>The environment variable a randomised run sets. Unset ⇒ <see cref="DefaultSeed"/>.</summary>
    public const string EnvVar = "TYPHON_TEST_SEED";

    /// <summary>
    /// The seed used when nothing overrides it. Arbitrary but FIXED — its value does not matter, its constancy does: it is what keeps the gate reproducible.
    /// </summary>
    public const int DefaultSeed = 0x5EED_0001;

    private static readonly int Seed = ReadRunSeed();

    /// <summary>The seed for this whole process. Print it, and every derived value in the run can be reproduced.</summary>
    public static int RunSeed => Seed;

    /// <summary>True when the run seed came from the environment rather than the default — i.e. this is an exploring run.</summary>
    public static bool IsRandomised => Seed != DefaultSeed;

    private static int ReadRunSeed()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultSeed;
        }

        // A malformed value must be LOUD. Silently falling back to the default would make a nightly report "seed 12345" while running the pinned state — a
        // false green of exactly the kind this epic exists to remove.
        if (!int.TryParse(raw, out var parsed))
        {
            throw new InvalidOperationException(
                $"{EnvVar} is set to '{raw}', which is not a 32-bit integer. Set it to a number or unset it; falling back silently would report a seed the "
                + "run did not actually use.");
        }

        return parsed;
    }

    /// <summary>
    /// A seed for the currently-running test, stable across processes and platforms. Two different tests get different seeds in the same run; the same test
    /// gets the same seed whenever the run seed matches.
    /// </summary>
    /// <param name="purpose">
    /// Distinguishes several independent random streams inside one test (e.g. <c>"workload"</c> and <c>"crash-points"</c>), so adding a second stream does not
    /// perturb the first one's values and invalidate a stored repro.
    /// </param>
    public static int For(string purpose = null)
    {
        var test = TestContext.CurrentContext?.Test;
        var name = test?.FullName ?? test?.Name ?? "unknown-test";
        return Derive(Seed, name, purpose);
    }

    /// <summary>A <see cref="Random"/> seeded by <see cref="For"/> — the common case.</summary>
    public static Random Random(string purpose = null) => new(For(purpose));

    /// <summary>
    /// The command that reproduces the current test exactly. Put this in the failure message of anything seeded: a randomised failure whose repro is not
    /// attached to the failure itself costs more than the exploration bought.
    /// </summary>
    public static string Repro
    {
        get
        {
            var name = TestContext.CurrentContext?.Test?.FullName ?? "<test>";
            return $"{EnvVar}={Seed} dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj --filter \"FullyQualifiedName~{name}\"";
        }
    }

    /// <summary>
    /// A stable hash of a string, for callers that need a reproducible NAME rather than a seed — chiefly the fixtures that derive a temp-database name from
    /// the test name.
    /// </summary>
    /// <remarks>
    /// Those sites used <c>string.GetHashCode()</c>, which is randomised per process, so each run created a differently-named database: stale directories
    /// accumulate, and a crash artifact cannot be found twice under the same name. The value here is a fixed function of the string.
    /// </remarks>
    public static uint StableHash(string value)
    {
        var hash = OffsetBasis;
        Mix(ref hash, Encoding.UTF8.GetBytes(value ?? string.Empty));
        return hash;
    }

    /// <summary>
    /// FNV-1a over the UTF-8 bytes of the inputs. Chosen for being a fixed, documented function rather than for its distribution — a test seed needs
    /// reproducibility, not avalanche behaviour.
    /// </summary>
    internal static int Derive(int runSeed, string name, string purpose)
    {
        var hash = OffsetBasis;
        Mix(ref hash, BitConverter.GetBytes(runSeed));
        Mix(ref hash, Encoding.UTF8.GetBytes(name ?? string.Empty));
        if (purpose != null)
        {
            Mix(ref hash, [0]); // a separator, so For("ab") and For(null) on a test named "…ab" cannot collide
            Mix(ref hash, Encoding.UTF8.GetBytes(purpose));
        }

        // Mask to 31 bits: several call sites feed this to `new Random(seed)` and to arithmetic that a negative value would make awkward, and losing one bit
        // of a test seed costs nothing.
        return (int)(hash & 0x7FFF_FFFF);
    }

    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    private static void Mix(ref uint hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= Prime;
        }
    }
}

/// <summary>
/// Attaches the seed repro to a fixture's output, and to the result of any test in it that fails.
/// </summary>
/// <remarks>
/// An <see cref="ITestAction"/> rather than a base-class hook because the seeded fixtures do not share one base — <c>WalCrashSweepTests</c> derives from
/// nothing, <c>AxisArchetypesTests</c> from <c>TestBase&lt;T&gt;</c>. Writing to the test's own output (rather than stdout) is deliberate: #703 measured that
/// stdout is not reliably captured under the runner, and a repro line nobody can find is the same as no repro line.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SeededAttribute : Attribute, ITestAction
{
    /// <summary>Applies to each test case, so the repro names the individual case rather than the fixture.</summary>
    public ActionTargets Targets => ActionTargets.Test;

    /// <inheritdoc/>
    public void BeforeTest(ITest test)
    {
    }

    /// <inheritdoc/>
    public void AfterTest(ITest test)
    {
        var result = TestContext.CurrentContext?.Result;
        if (result == null || result.Outcome.Status != TestStatus.Failed)
        {
            return;
        }

        TestContext.Out.WriteLine($"[seed] run seed {TestSeed.RunSeed}{(TestSeed.IsRandomised ? " (randomised)" : " (default)")}");
        TestContext.Out.WriteLine($"[seed] reproduce with: {TestSeed.Repro}");
    }
}
