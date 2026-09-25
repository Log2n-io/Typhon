using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// SP-4 (Realms): a realm's structures are sized from its config, so the thousands of small realms an application may host (one per building
/// interior, per dungeon instance) cost bytes, not the tens of kilobytes a world-sized grid starts with.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmFootprintTests
{
    private static SpatialGridConfig OneCell() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(20, 20), 32f);

    /// <summary>A one-cell realm: grid, registration, one archetype's realm state, the cell and its first cluster.</summary>
    private static long OneCellRealmBytes(RealmTable table, ushort id)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var grid = new SpatialGrid(OneCell());
        table.Register(new RealmId(id), grid);
        var rs = new RealmArchetypeSpatial(null, new RealmId(id), grid);
        var key = grid.WorldToCellKey(5, 5, 0);
        rs.CellClusterPool.AddCluster(key, 7);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public void OneCellRealm_WithOneArchetypeAndOneCluster_AllocatesUnder4KiB()
    {
        var table = new RealmTable(4);
        OneCellRealmBytes(table, 0);   // JIT and type-init allocations land on the first call, not in the measured one

        var bytes = OneCellRealmBytes(table, 1);

        // Measured 1.2 KB on x64 (was ~50 KB before SP-4: a 16 KiB CellState chunk, ~25 KiB of hash-map stripes, a 5.5 KiB pool).
        Assert.That(bytes, Is.LessThan(4096), $"a one-cell realm allocated {bytes} B");
    }

    [Test]
    public void FiveThousandOneCellRealms_Under20MB()
    {
        const int realms = 5000;
        var table = new RealmTable(realms + 1);
        OneCellRealmBytes(table, 0);
        long total = 0;
        for (ushort i = 1; i <= realms; i++)
        {
            total += OneCellRealmBytes(table, i);
        }

        Assert.That(total, Is.LessThan(20L << 20), $"{realms} one-cell realms allocated {total / 1024} KiB");
    }

    /// <summary>The dense directory (small worlds) and the hash map (large ones) must answer every read identically — and neither may create on a read.</summary>
    [VerifiesRule("VG-01")]
    [TestCase(64, true, TestName = "DenseDirectory")]
    [TestCase(2100, false, TestName = "HashMapDirectory")]
    public void BlockDirectory_ReadPathsDoNotCreate_AndNeighboursResolveAcrossAbsentBlocks(int cellsPerSide, bool dense)
    {
        var grid = new SpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(cellsPerSide, cellsPerSide), 1f));
        Assert.That(grid.UsesDenseDirectory, Is.EqualTo(dense), "the case must exercise the directory form it is named after");

        // Three cells far apart, in three different blocks.
        var a = grid.WorldToCellKey(0.5, 0.5, 0);
        var b = grid.WorldToCellKey(cellsPerSide - 0.5, 0.5, 0);
        var c = grid.WorldToCellKey(cellsPerSide - 0.5, cellsPerSide - 0.5, 0);
        Assert.That(new HashSet<int> { a, b, c }, Has.Count.EqualTo(3));
        var cells = grid.CellCount;
        var blocks = grid.BlockCount;

        // VG-02: reads never create.
        Assert.That(grid.TryGetCellKey(cellsPerSide / 2, cellsPerSide / 2, 0, out _), Is.False);
        Assert.That(grid.TryGetCellKeyAt(cellsPerSide / 2.0, cellsPerSide / 2.0, 0, out _), Is.False);
        Assert.That(grid.CellCount, Is.EqualTo(cells));
        Assert.That(grid.BlockCount, Is.EqualTo(blocks));

        // Existing cells resolve to themselves.
        Assert.That(grid.TryGetCellKey(0, 0, 0, out var ka) && ka == a, Is.True);
        Assert.That(grid.TryGetCellKey(cellsPerSide - 1, cellsPerSide - 1, 0, out var kc) && kc == c, Is.True);

        // VG-01: a neighbour step into an ABSENT BLOCK answers "no cell", never a stale or foreign one — and the cell once it exists. Cell (15, 0) sits
        // at the edge of block (0, 0); its +X neighbour (16, 0) is in block (1, 0), which nothing has created.
        var edge = grid.WorldToCellKey(15.5, 0.5, 0);
        Assert.That(grid.TryGetNeighbourCellKey(edge, 1, 0, 0, out _), Is.False);
        var across = grid.WorldToCellKey(16.5, 0.5, 0);
        Assert.That(grid.TryGetNeighbourCellKey(edge, 1, 0, 0, out var appeared) && appeared == across, Is.True, "absent, then appears");

        // After a reset the directory is empty again, in both forms.
        grid.ResetCellState();
        Assert.That(grid.TryGetCellKey(0, 0, 0, out _), Is.False);
        Assert.That(grid.WorldToCellKey(0.5, 0.5, 0), Is.EqualTo(0), "the first cell after a reset is slot 0");
    }
}
