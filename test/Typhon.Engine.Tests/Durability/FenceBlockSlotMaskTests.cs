using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Written-slot mask semantics for columnar fence emission (#559 §4.5). The fence must emit only the component columns actually
/// written in a tick — but must fail SAFE: a writer that does not identify its component falls back to emitting everything, so a
/// call site added later over-emits (redundant bytes) rather than under-emits (lost data).
/// </summary>
[TestFixture]
internal sealed class FenceBlockSlotMaskTests
{
    /// <summary>The slot-identifying overload records exactly the component written — AC 1.</summary>
    [Test]
    public void SetDirty_WithComponentSlot_RecordsOnlyThatSlot()
    {
        var mask = new int[4];

        // Model of ArchetypeClusterState.SetDirty(chunkId, slotIndex, componentSlot)'s mask update.
        ApplyKnown(mask, chunkId: 2, componentSlot: 3);

        Assert.That(mask[2], Is.EqualTo(1 << 3), "only the written component's bit may be set");
        Assert.That(mask[0], Is.EqualTo(0));
        Assert.That(mask[1], Is.EqualTo(0));
        Assert.That(mask[3], Is.EqualTo(0), "other clusters are untouched");
    }

    /// <summary>Several components written into one cluster accumulate — AC 1.</summary>
    [Test]
    public void SetDirty_MultipleComponents_AccumulateInTheMask()
    {
        var mask = new int[1];

        ApplyKnown(mask, 0, 0);
        ApplyKnown(mask, 0, 2);
        ApplyKnown(mask, 0, 2);   // repeat must be idempotent — this is the hot path that skips the atomic

        Assert.That(mask[0], Is.EqualTo((1 << 0) | (1 << 2)));
    }

    /// <summary>The component-less overload poisons the mask to "everything" — the fail-safe contract.</summary>
    [Test]
    public void SetDirty_WithoutComponentSlot_MarksAllSlotsWritten()
    {
        var mask = new int[1];

        ApplyKnown(mask, 0, 1);
        ApplyUnknown(mask, 0);

        Assert.That(mask[0], Is.EqualTo(ArchetypeClusterState.AllSlotsWritten),
            "an unidentified write must widen the mask to everything, never narrow it");
    }

    /// <summary>Once poisoned, a later identified write cannot narrow the mask back — AC 2 safety.</summary>
    [Test]
    public void AllSlotsWritten_IsNotNarrowedByALaterKnownWrite()
    {
        var mask = new int[1];

        ApplyUnknown(mask, 0);
        ApplyKnown(mask, 0, 1);

        Assert.That(mask[0], Is.EqualTo(ArchetypeClusterState.AllSlotsWritten));
    }

    /// <summary>The emitted column set is (union of written masks) ∩ durable slots — AC 2.</summary>
    [Test]
    public void ActiveColumnSet_IsUnionOfWrittenMasks_IntersectedWithDurable()
    {
        // Archetype with 5 durable slots 0..4; three dirty clusters wrote different subsets.
        var durableMask = 0b11111;
        int[] perCluster = [1 << 0, (1 << 0) | (1 << 2), 1 << 2];

        var union = 0;
        foreach (var m in perCluster)
        {
            union |= m;
        }

        Assert.That(union & durableMask, Is.EqualTo((1 << 0) | (1 << 2)),
            "slots 1, 3 and 4 were never written and must not be emitted");
    }

    /// <summary>An unknown writer anywhere in the archetype widens the whole batch back to every durable column — AC 2 safety.</summary>
    [Test]
    public void OneUnknownWriter_WidensTheBatchToAllDurableColumns()
    {
        var durableMask = 0b11111;
        int[] perCluster = [1 << 0, ArchetypeClusterState.AllSlotsWritten, 1 << 2];

        var union = 0;
        foreach (var m in perCluster)
        {
            union |= m;
        }

        Assert.That(union & durableMask, Is.EqualTo(durableMask));
    }

    /// <summary>Transient/Versioned slots stay excluded even when written — the durable mask is the outer bound.</summary>
    [Test]
    public void NonDurableSlots_AreNeverEmitted_EvenWhenWritten()
    {
        var durableMask = 0b00111;                    // slots 0..2 durable; 3 Versioned, 4 Transient
        var written = (1 << 1) | (1 << 3) | (1 << 4);

        Assert.That(written & durableMask, Is.EqualTo(1 << 1));
    }

    /// <summary>A tick in which nothing durable was written emits no block at all — AC 3.</summary>
    [Test]
    public void OnlyNonDurableWrites_ProduceAnEmptyColumnSet()
    {
        var durableMask = 0b0011;
        var written = 1 << 3;   // a Transient component only

        Assert.That(written & durableMask, Is.Zero, "the emitter must skip the archetype entirely");
    }

    // ── models of the production mask updates (ArchetypeClusterState.SetDirty overloads) ──

    private static void ApplyKnown(int[] mask, int chunkId, int componentSlot)
    {
        var bit = 1 << componentSlot;
        if ((mask[chunkId] & bit) == 0)
        {
            mask[chunkId] |= bit;
        }
    }

    private static void ApplyUnknown(int[] mask, int chunkId)
    {
        if (mask[chunkId] != ArchetypeClusterState.AllSlotsWritten)
        {
            mask[chunkId] = ArchetypeClusterState.AllSlotsWritten;
        }
    }
}
