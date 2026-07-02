using NUnit.Framework;
using System;
using System.Reflection;
using System.Threading;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Unit tests for the strict-mode gate and primitives (#422). The primitives take the gate as an explicit argument, so their
/// behavior is verified directly here without depending on the process-wide <see cref="CheckConfig.Enabled"/> value (which is a
/// <c>static readonly</c> baked once at load).
/// </summary>
[TestFixture]
[NonParallelizable]   // reads/writes the process-static CheckConfig.RecordedViolationCount
public class CheckConfigTests
{
    // ── Suite-config canary ──────────────────────────────────────────────────────

    [Test]
    public void SuiteConfig_HasStrictModeEnabled()
    {
        // The test project's typhon.telemetry.json enables strict mode suite-wide so the converted guards are actively
        // exercised. The integration tests (StrictModeMisuseTests, SystemAccessValidatorTests) use Assume — Inconclusive, not
        // fail — when the gate is off. This canary fails LOUDLY if the config regresses (json not copied, key typo, loader
        // change), so the coverage loss can't hide behind a green suite.
        Assert.That(CheckConfig.Enabled, Is.True, "typhon.telemetry.json must set Typhon:Checks:Enabled=true for the test suite.");
        Assert.That(CheckConfig.DeclaredAccessActive, Is.True, "typhon.telemetry.json must set Typhon:Checks:DeclaredAccess=true for the test suite.");
    }

    // ── Gate-field shape (the JIT-fold invariant) ────────────────────────────────

    [Test]
    public void GateFields_AreStaticReadonlyBool()
    {
        foreach (var name in new[] { nameof(CheckConfig.Enabled), nameof(CheckConfig.DeclaredAccessActive) })
        {
            var field = typeof(CheckConfig).GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"{name} must exist as a public static field.");
            Assert.That(field.FieldType, Is.EqualTo(typeof(bool)), $"{name} must be bool so the JIT can fold `if (!{name})`.");
            Assert.That(field.IsInitOnly, Is.True, $"{name} must be `static readonly` for JIT dead-code elimination.");
            Assert.That(field.IsLiteral, Is.False, $"{name} must be `static readonly`, not `const` (const breaks runtime configurability).");
        }
    }

    // ── Require ──────────────────────────────────────────────────────────────────

    [Test]
    public void Require_Enabled_ConditionFalse_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CheckConfig.Require(true, false, $"boom {42}"));
        Assert.That(ex.Message, Does.Contain("boom 42"));
    }

    [Test]
    public void Require_Enabled_ConditionTrue_DoesNotThrow()
        => Assert.DoesNotThrow(() => CheckConfig.Require(true, true, $"should not fire {42}"));

    [Test]
    public void Require_Disabled_DoesNotThrow_EvenWhenConditionFalse()
        => Assert.DoesNotThrow(() => CheckConfig.Require(false, false, $"gate off {42}"));

    // ── Lazy message: interpolation arguments are NOT evaluated on the passing path ──

    [Test]
    public void Require_PassingPath_DoesNotEvaluateMessageArguments()
    {
        int sideEffects = 0;
        int Probe()
        {
            sideEffects++;
            return sideEffects;
        }

        // Condition holds → the message (and its side-effecting argument) must not be built.
        CheckConfig.Require(true, true, $"never {Probe()}");
        Assert.That(sideEffects, Is.Zero, "Message argument must not be evaluated when the check passes (lazy message).");

        // Gate off → likewise skipped.
        CheckConfig.Require(false, false, $"never {Probe()}");
        Assert.That(sideEffects, Is.Zero, "Message argument must not be evaluated when the gate is off.");

        // Failing path → the argument IS evaluated (message is built for the throw).
        Assert.Throws<InvalidOperationException>(() => CheckConfig.Require(true, false, $"fired {Probe()}"));
        Assert.That(sideEffects, Is.EqualTo(1), "Message argument is evaluated exactly once on the throwing path.");
    }

    // ── Record (latch-safe, never throws) ────────────────────────────────────────

    [Test]
    public void Record_Enabled_ConditionFalse_IncrementsCounter_NeverThrows()
    {
        long before = Interlocked.Read(ref CheckConfig.RecordedViolationCount);
        Assert.DoesNotThrow(() => CheckConfig.Record(true, false, $"violation {1}"));
        long after = Interlocked.Read(ref CheckConfig.RecordedViolationCount);
        Assert.That(after, Is.GreaterThan(before));
    }

    [Test]
    public void Record_ConditionTrue_DoesNotIncrement()
    {
        long before = Interlocked.Read(ref CheckConfig.RecordedViolationCount);
        CheckConfig.Record(true, true, $"no {1}");
        CheckConfig.Record(false, false, $"no {1}");
        long after = Interlocked.Read(ref CheckConfig.RecordedViolationCount);
        Assert.That(after, Is.EqualTo(before));
    }
}
