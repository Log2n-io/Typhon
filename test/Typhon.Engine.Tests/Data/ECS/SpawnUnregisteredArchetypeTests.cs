using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// A component and an archetype that NO fixture registers. That is the whole point: Archetype<SpawnUnregArch>.Metadata
// stays null, which is the state a user who forgot RegisterComponentFromAccessor / InitializeArchetypes is in.
[Component("Typhon.Test.Unregistered.Mark", 1, StorageMode = StorageMode.SingleVersion)]
struct SpawnUnregMark
{
    public int Value;
}

[Archetype]
partial class SpawnUnregArch : Archetype<SpawnUnregArch>
{
    public static readonly Comp<SpawnUnregMark> Mark = Register<SpawnUnregMark>();
}

/// <summary>
/// #1095: spawning an unregistered archetype is refused with a message, not a <see cref="NullReferenceException"/> from
/// engine internals — and refused with strict mode OFF, which is what the Release NuGet ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> All three spawn entry points read <c>Archetype&lt;TArch&gt;.Metadata</c>, checked it for null behind
/// <c>CheckConfig.Require(CheckConfig.Enabled, …)</c>, and then dereferenced it — <c>meta!.ArchetypeId</c> — a line or two
/// later. <c>CheckConfig.Enabled</c> is off by default so the JIT deletes the check, which left the most ordinary mistake
/// there is (forgetting to register) surfacing as an NRE attributed to the engine.
/// </para>
/// <para>
/// <b>Same defect as #897</b>, which did this to the four fluent spatial predicates. The distinguishing test is the one
/// #897 states: a strict-mode gate is the right home for a check whose ungated failure is silence, and the wrong home for
/// one whose ungated failure is a crash.
/// </para>
/// <para>
/// <b><c>[Category("ChecksOffGated")]</c> is what makes these cases evidence.</b> This suite's <c>typhon.telemetry.json</c>
/// turns strict mode on for every fixture, so a case asserting the throw would pass against the broken code too. The gate
/// runs this category in a forked process with <c>TYPHON__CHECKS__ENABLED=false</c>.
/// </para>
/// <para>
/// <b>Which of the two conditions actually fires, because the obvious reading is backwards.</b> These cases were first
/// written assuming <c>Archetype&lt;TArch&gt;.Metadata</c> would be null for an unregistered archetype, and three of them
/// skipped on that assumption while the fourth still produced an NRE — which is how the real shape came out.
/// <c>Metadata</c> is <c>_metadata ?? EnsureFinalized()</c>, and <c>EnsureFinalized</c> registers the archetype in the
/// process catalog <i>on read</i>, so asking for it makes it non-null. A declared archetype always has metadata. What
/// "forgot to register" means is metadata present and this database's <c>_archetypeStates</c> slot empty — so the
/// condition under test is the <c>EntityMap</c> one, and the null-metadata branch is a guard against an engine-side
/// registry failure rather than a user mistake.
/// </para>
/// <para>
/// <b>Which also made the fix free.</b> The cost note that used to sit beside the guard said the gate had to
/// short-circuit before the array index, since the index can throw and cannot be folded. True, but irrelevant: the index
/// is paid regardless, because <c>SpawnInternal</c> opens with <c>_dbe._archetypeStates[meta.ArchetypeId]</c> and both
/// batch paths do the same a few lines in.
/// </para>
/// </remarks>
[NonParallelizable]
[Category("ChecksOffGated")]
class SpawnUnregisteredArchetypeTests : TestBase<SpawnUnregisteredArchetypeTests>
{
    /// <summary>
    /// An engine initialized for <c>ClAnt</c>'s components and never told about <see cref="SpawnUnregMark"/>. Both halves
    /// matter: the missing one is the subject, and the present one gives the last case a registered archetype to prove the
    /// refusal did not poison the transaction.
    /// </summary>
    private DatabaseEngine SetupEngineWithoutIt()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void AssertRefused(Action spawn)
    {
        var ex = Assert.Throws<InvalidOperationException>(spawn);
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(nameof(SpawnUnregArch)), "the message must name the archetype the caller wrote");
            Assert.That(ex.Message, Does.Contain("not registered with this database"), "and say what is wrong");
            Assert.That(ex.Message, Does.Contain("InitializeArchetypes"), "and name the step that fixes it");
        });
    }

    [Test]
    public void Spawn_OnAnUnregisteredArchetype_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False, "the point of the case is that the guard holds with strict mode off");
        using var dbe = SetupEngineWithoutIt();
        using var tx = dbe.CreateQuickTransaction();
        AssertRefused(() => tx.Spawn<SpawnUnregArch>());
    }

    [Test]
    public void SpawnBatch_OnAnUnregisteredArchetype_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngineWithoutIt();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[2];
        AssertRefused(() => tx.SpawnBatch<SpawnUnregArch>(ids));
    }

    [Test]
    public void SpawnBatchAllocate_OnAnUnregisteredArchetype_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngineWithoutIt();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[2];
        AssertRefused(() => tx.SpawnBatchAllocate<SpawnUnregArch>(2, ids));
    }

    /// <summary>
    /// The refusal is a diagnostic, not a latch: the transaction must still work afterwards, or a tool that catches the
    /// message and carries on is broken by the fix meant to help it.
    /// </summary>
    [Test]
    public void TheRefusalLeavesTheTransactionUsable()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngineWithoutIt();
        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() => tx.Spawn<SpawnUnregArch>());

        var pos = new ClPosition(1, 2);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos));
        Assert.That(id.IsNull, Is.False, "the same transaction still spawns a registered archetype");
    }
}
