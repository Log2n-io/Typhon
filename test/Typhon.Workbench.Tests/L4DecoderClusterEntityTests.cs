using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Storage.Decoders;

namespace Typhon.Workbench.Tests;

// Pure, fixture-free coverage of the L5 cluster entity-content decoder (file-map §10 Q4 override). The HTTP-level
// GetClusterEntity tests in StorageMapDetailTests exercise the full wire path but skip on the cluster-less demo DB;
// these drive L4Decoder.DecodeClusterEntity directly over a hand-built chunk so the genuinely-new structural logic
// — occupancy gating, the leading entity-PK cell, per-component headers (enabled / disabled), and transient-slot
// skipping — runs deterministically every build. The per-field byte extraction is byte-identical to DecodeComponent
// (already covered) and its SoA addressing is pinned by the engine's TryGetClusterEntityLayout test, so passing a null
// definition here (header-only) isolates exactly the new code.
[TestFixture]
public sealed class L4DecoderClusterEntityTests
{
    private const int ClusterSize = 8;
    private const int ComponentCount = 3;
    private const int HeaderSize = 8 + 8 * ComponentCount; // OccupancyBits + EnabledBits[C]
    private const int EntityIdsOffset = HeaderSize;

    // Slot 2 holds entity 123456789; component 0 enabled, component 1 disabled, component 2 is transient.
    private const int OccupiedSlot = 2;
    private const long EntityId = 123456789L;

    private static (string Name, int Offset, int Size, bool Transient, DBComponentDefinition Definition)[] Components() =>
    [
        ("PosComp", 96, 8, false, null),
        ("VelComp", 104, 8, false, null),
        ("TransientComp", 112, 8, true, null),
    ];

    private static byte[] BuildChunk()
    {
        var buf = new byte[256];
        // OccupancyBits @0 — only slot 2 live.
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), 1UL << OccupiedSlot);
        // EnabledBits[0] @8 — component 0 enabled for slot 2.
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8), 1UL << OccupiedSlot);
        // EnabledBits[1] @16 — component 1 left clear ⇒ disabled for slot 2.
        // EnabledBits[2] @24 — component 2 is transient; its enabled state is irrelevant (skipped).
        // EntityKeys[2] @ EntityIdsOffset + 2*8.
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(EntityIdsOffset + OccupiedSlot * 8), EntityId);
        return buf;
    }

    [Test]
    public void DecodeClusterEntity_OccupiedSlot_EmitsPkThenPerComponentHeaders()
    {
        var cells = L4Decoder.DecodeClusterEntity(BuildChunk(), OccupiedSlot, ClusterSize, EntityIdsOffset, Components());

        // entityPk + one header per component (no field cells — definitions are null here).
        Assert.That(cells.Length, Is.EqualTo(1 + ComponentCount));

        Assert.That(cells[0].Kind, Is.EqualTo("entityPk"));
        Assert.That(cells[0].Value, Is.EqualTo(EntityId.ToString()));

        var pos = Array.Find(cells, c => c.Label == "PosComp");
        Assert.That(pos, Is.Not.Null);
        Assert.That(pos.Kind, Is.EqualTo("componentHeader"));
        Assert.That(pos.Value, Is.EqualTo("enabled"), "EnabledBits[0] sets slot 2");

        var vel = Array.Find(cells, c => c.Label == "VelComp");
        Assert.That(vel, Is.Not.Null);
        Assert.That(vel.Value, Is.EqualTo("disabled"), "EnabledBits[1] leaves slot 2 clear");

        var transient = Array.Find(cells, c => c.Label == "TransientComp");
        Assert.That(transient, Is.Not.Null);
        Assert.That(transient.Kind, Is.EqualTo("componentHeader"));
        Assert.That(transient.Value, Does.Contain("transient"), "transient component data is not in the persisted chunk");
        Assert.That(transient.Value, Does.Contain("not persisted"));
    }

    [Test]
    public void DecodeClusterEntity_FreeSlot_ReturnsEmpty()
    {
        // Slot 5 is not set in OccupancyBits → no live entity.
        var cells = L4Decoder.DecodeClusterEntity(BuildChunk(), 5, ClusterSize, EntityIdsOffset, Components());
        Assert.That(cells, Is.Empty);
    }

    [Test]
    public void DecodeClusterEntity_SlotOutOfRange_ReturnsEmpty()
    {
        var cells = L4Decoder.DecodeClusterEntity(BuildChunk(), ClusterSize, ClusterSize, EntityIdsOffset, Components());
        Assert.That(cells, Is.Empty);
    }
}
