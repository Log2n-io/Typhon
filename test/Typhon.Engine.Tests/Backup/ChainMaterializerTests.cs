using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// Materialising a point (plan 1413 §2.7, 04 §1) against a reference model: the newest pack holding a page wins, a page no pack holds is zeros,
/// the target is exactly <c>N × 8 KiB</c>, and no damaged byte is ever written silently.
/// </summary>
[TestFixture]
internal sealed class ChainMaterializerTests
{
    private static byte[] Materialize(SyntheticChain chain, int index, MaterializeOptions options = null, InMemorySparseFileSystem target = null)
    {
        target ??= NewTarget();
        var packs = chain.OpenChain(chain.Points[index].BackupId);
        try
        {
            using var file = target.OpenCreateNew("/t/data");
            ChainMaterializer.Materialize(packs, file, options);
        }
        finally
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }
        }

        return target.ReadAll("/t/data");
    }

    private static InMemorySparseFileSystem NewTarget()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/t");
        return fs;
    }

    private static void AssertImage(SyntheticChain chain, int index, byte[] data)
    {
        var n = chain.ExpectedPageCount(index);
        Assert.That(data.Length, Is.EqualTo(n * PackFormat.PageSize), $"point {index + 1}: length");
        for (var p = 0; p < n; p++)
        {
            if (!data.AsSpan(p * PackFormat.PageSize, PackFormat.PageSize).SequenceEqual(chain.ExpectedPage(index, p)))
            {
                Assert.Fail($"point #{chain.Points[index].BackupId}: page {p} differs from the model");
            }
        }
    }

    [TestCase(1, 11)]
    [TestCase(2, 12)]
    [TestCase(7, 13)]
    [TestCase(50, 14)]
    [CancelAfter(30_000)]
    public void EveryPointOfAChain_MaterialisesToTheModel(int depth, int seed)
    {
        var chain = SyntheticChain.Build(depth, seed);
        for (var i = 0; i < depth; i++)
        {
            // A small read budget: windows refill constantly, as on a deep chain.
            AssertImage(chain, i, Materialize(chain, i, new MaterializeOptions { ReadBudgetBytes = 1 }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void HolesAreZeros_AndCounted()
    {
        var chain = SyntheticChain.Build(12, 77);
        var last = chain.Points.Count - 1;
        var packs = chain.OpenChain(chain.Points[last].BackupId);
        var target = NewTarget();
        MaterializeResult result;
        using (var file = target.OpenCreateNew("/t/data"))
        {
            result = ChainMaterializer.Materialize(packs, file);
        }

        foreach (var p in packs)
        {
            p.Dispose();
        }

        var data = target.ReadAll("/t/data");
        AssertImage(chain, last, data);
        var zeros = Enumerable.Range(0, chain.ExpectedPageCount(last))
            .Count(p => data.AsSpan(p * PackFormat.PageSize, PackFormat.PageSize).IndexOfAnyExcept((byte)0) < 0);
        Assert.That(result.ZeroPages, Is.EqualTo(zeros));
        Assert.That(result.Pages, Is.EqualTo(chain.ExpectedPageCount(last)));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADamagedFrame_FailsTheMaterialisation_UnlessForensicsAskForIt()
    {
        var chain = SyntheticChain.Build(4, 21);
        var victim = chain.Points[1];
        using (var reader = PackReader.Open(chain.Fs, victim.PackPath))
        {
            var entry = reader.LoadIndex().First(e => e.StoredLength > 0 && e.PageId >= 2);
            chain.Fs.Poke(victim.PackPath, entry.DataOffset + 1, [0x77]);
        }

        // Point 2 itself, and point 3 only if the damaged page is not overwritten later: point 2 is enough to prove the check.
        var ex = Assert.Throws<BackupPackCorruptException>(() => Materialize(chain, 1));
        Assert.That(InMemorySparseFileSystem.Normalize(ex.PackFile), Is.EqualTo(InMemorySparseFileSystem.Normalize(victim.PackPath)));
        Assert.That(ex.Section, Is.EqualTo("Page"));

        var packs = chain.OpenChain(victim.BackupId);
        var target = NewTarget();
        MaterializeResult result;
        using (var file = target.OpenCreateNew("/t/data"))
        {
            result = ChainMaterializer.Materialize(packs, file, new MaterializeOptions { AllowDamaged = true });
        }

        foreach (var p in packs)
        {
            p.Dispose();
        }

        Assert.That(result.Damaged.Select(d => (long)d.PageId), Is.EqualTo(new[] { ex.PageId }));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnIndexEntryMovedToAnotherPage_IsCaughtByTheIndexCrc()
    {
        // The frame is sound and its PageCrc matches: only the INDEX's own CRC can tell that the page lands at the wrong id.
        var chain = SyntheticChain.Build(3, 31);
        var victim = chain.Points[2];
        using (var reader = PackReader.Open(chain.Fs, victim.PackPath))
        {
            var index = reader.LoadIndex();
            var last = index.Length - 1;
            var moved = index[last].PageId + 1;
            Assume.That(moved, Is.LessThan((uint)reader.Header.ImagePages));
            var offset = reader.Footer.IndexOffset + ((long)last * PackFormat.PageEntrySize);
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, moved);
            chain.Fs.Poke(victim.PackPath, offset, bytes);
        }

        var ex = Assert.Throws<BackupPackCorruptException>(() => Materialize(chain, 2));
        Assert.That(ex.Section, Is.EqualTo("Index"));
        Assert.That(ex.Message, Does.Contain("IndexCRC"));
    }
}
