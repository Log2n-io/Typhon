using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The crash-open reset of root lock words (PS-20) clears a held root's lock and nothing else. A chunk no other chunk names is not always a root: a
/// buffer's first free chunk, an orphan, or a chunk whose predecessor's link a crash lost — its bytes at the lock's offset are data.
/// </summary>
[TestFixture]
internal sealed class LockWordRecoveryTests : TestBase<LockWordRecoveryTests>
{
    private DatabaseEngine _dbe;

    // No periodic checkpoint: nothing but the test writes the segments.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    [SetUp]
    public void SetUpEngine()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<MkTrader>();
        _dbe.RegisterComponentFromAccessor<MkWallet>();
        _dbe.InitializeArchetypes();
    }

    [TearDown]
    public void TearDownEngine()
    {
        _dbe?.Dispose();
        _dbe = null;
    }

    private delegate TR WithAccessor<out TR>(ref ChunkAccessor<PersistentStore> accessor);

    private TR Write<TR>(ChunkBasedSegment<PersistentStore> segment, WithAccessor<TR> action)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var changeSet = _dbe.MMF.CreateChangeSet();
        var accessor = segment.CreateChunkAccessor(changeSet);
        try
        {
            return action(ref accessor);
        }
        finally
        {
            accessor.Dispose();
            changeSet.ReleaseDirtyMarks();
        }
    }

    /// <summary>An allocated chunk nothing names, its bytes from <paramref name="offset"/> set to 0xAB: data where a root would hold its lock.</summary>
    private unsafe int Orphan(ChunkBasedSegment<PersistentStore> segment, int offset, int length) => Write(segment, (ref ChunkAccessor<PersistentStore> a) =>
    {
        var chunk = segment.AllocateChunk(false, a.ChangeSet);
        var address = a.GetChunkAddress(chunk, true);
        new Span<byte>(address, segment.Stride).Clear();
        new Span<byte>(address + offset, length).Fill(0xAB);
        return chunk;
    });

    private unsafe bool AllAb(ChunkBasedSegment<PersistentStore> segment, int chunk, int offset, int length) => Write(segment,
        (ref ChunkAccessor<PersistentStore> a) => new ReadOnlySpan<byte>(a.GetChunkAddress(chunk) + offset, length).IndexOfAnyExcept((byte)0xAB) < 0);

    /// <summary>The root chunk of the entity's <see cref="MkWallet"/> revision chain, from its entity record.</summary>
    private unsafe int WalletRootOf(EntityId entityId)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var ct = _dbe.GetComponentTable<MkWallet>();
        var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        var slot = -1;
        for (var s = 0; s < meta.ComponentCount; s++)
        {
            if (ReferenceEquals(es.SlotToComponentTable[s], ct))
            {
                slot = s;
                break;
            }
        }

        var record = stackalloc byte[meta._entityRecordSize];
        var accessor = es.EntityMap.Segment.CreateChunkAccessor();
        var found = es.EntityMap.TryGet(entityId.EntityKey, record, ref accessor);
        accessor.Dispose();
        Assert.That(found, Is.True, "the entity's record");
        return meta.IsClusterEligible && es.ClusterState != null
            ? ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[slot])
            : EntityRecordAccessor.GetLocation(record, slot);
    }

    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(10_000)]
    public void TheBufferReset_ClearsAHeldRoot_AndNoDataChunk()
    {
        var vsbs = _dbe.GetComponentCollectionVSBS<long>();
        var segment = vsbs.Segment;
        var buffer = Write(segment, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            for (var i = 1; i <= 20; i++)
            {
                vsbs.AddElement(id, i, ref a);
            }

            a.GetChunk<VariableSizedBufferRootHeader>(id, true).EnterBufferLockForTest();
            return id;
        });
        var orphan = Orphan(segment, 8, 8);

        Assert.That(VariableSizedBufferRecovery.ResetRootLocksAfterCrash(segment, _dbe), Is.EqualTo(1), "the held root, and only it");
        Assert.That(Write(segment, (ref ChunkAccessor<PersistentStore> a) => a.GetChunk<VariableSizedBufferRootHeader>(buffer).Lock.LockedByThreadId),
            Is.Zero, "the root's lock, freed");
        Assert.That(AllAb(segment, orphan, 8, 8), Is.True, "the orphan's bytes at the lock's offset, untouched");
    }

    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(10_000)]
    public unsafe void TheChainReset_ClearsAHeldRoot_AndNoOverflowChunk()
    {
        EntityId id;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 1 }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 100 }));
            Assert.That(tx.Commit(), Is.True);
        }

        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var root = WalletRootOf(id);
        Write(segment, (ref ChunkAccessor<PersistentStore> a) =>
        {
            a.GetChunk<CompRevStorageHeader>(root, true).EnterControlLockForTest();
            return 0;
        });
        var overflow = Orphan(segment, 4, 4);
        Write(segment, (ref ChunkAccessor<PersistentStore> a) =>
        {
            // Named by another chunk: an overflow chunk, its bytes at the lock's offset an element's.
            var namer = segment.AllocateChunk(false, a.ChangeSet);
            var address = a.GetChunkAddress(namer, true);
            new Span<byte>(address, segment.Stride).Clear();
            *(int*)address = overflow;
            return namer;
        });

        Assert.That(ComponentRevisionManager.ResetChainLocksAfterCrash(segment, _dbe), Is.EqualTo(1), "the held root, and only it");
        Assert.That(Write(segment, (ref ChunkAccessor<PersistentStore> a) => a.GetChunk<CompRevStorageHeader>(root).Control.LockedByThreadId), Is.Zero,
            "the root's lock, freed");
        Assert.That(AllAb(segment, overflow, 4, 4), Is.True, "the overflow chunk's bytes at the lock's offset, untouched");
    }

    /// <summary>
    /// The reset walks a whole segment, which can be larger than the cache: it moves its epoch every <see cref="LockWordRecovery.PagesPerEpoch"/> pages
    /// chunks, so the pages it has passed do not stay epoch-protected. Held under one scope, the 29 GB SWG world's 6 GB revision segment left all 65,536
    /// slots of a 512 MiB cache protected and the crash open timed out on back-pressure. Measured from the root check, which the walk calls mid-pass:
    /// bounded with the batches, growing with the walk without them.
    /// </summary>
    [Test]
    // The batch size is thread-static: no other fixture sees it.
    [CancelAfter(10_000)]
    public void TheReset_MovesItsEpoch_SoAWalkLargerThanTheCacheKeepsNoPageProtected()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        const int chunks = 4_000;
        for (var made = 0; made < chunks; made += 500)
        {
            Write(segment, (ref ChunkAccessor<PersistentStore> a) =>
            {
                for (var i = 0; i < 500; i++)
                {
                    unsafe
                    {
                        var chunk = segment.AllocateChunk(false, a.ChangeSet);
                        var address = a.GetChunkAddress(chunk, true);
                        new Span<byte>(address, segment.Stride).Clear();
                        new Span<byte>(address + 4, 4).Fill(0xAB);   // a set word where a root holds its lock: the walk asks the root check
                    }
                }

                return 0;
            });
        }

        int PeakProtected(int chunksPerEpoch)
        {
            var saved = LockWordRecovery.ChunksPerEpochForTests;
            LockWordRecovery.ChunksPerEpochForTests = chunksPerEpoch;
            try
            {
                var peak = 0;
                LockWordRecovery.ResetRootLocks(segment, _dbe, 4, 4, (ref ChunkAccessor<PersistentStore> _, ChunkBasedSegment<PersistentStore> _, int _) =>
                {
                    peak = Math.Max(peak, _dbe.MMF.CountEpochProtectedPages());
                    return false;   // nothing is a root: the walk only reads
                });
                return peak;
            }
            finally
            {
                LockWordRecovery.ChunksPerEpochForTests = saved;
            }
        }

        var pages = (chunks * segment.Stride) / PagedMMF.PageSize;
        var unbatched = PeakProtected(int.MaxValue);
        var batched = PeakProtected(segment.Stride > 0 ? PagedMMF.PageSize / segment.Stride : 64);
        Assert.That(unbatched, Is.GreaterThan(pages / 2), $"without batches the walk protects what it passed ({pages} pages of chunks)");
        Assert.That(batched, Is.LessThanOrEqualTo(8), "with them, about the page it is on");
    }

    /// <summary>
    /// An allocated chunk nothing names is a root or a chunk no chain will link, its bytes read by no one: reset whatever its header. Filtered by a root's
    /// header check, a root whose overflow chunk the disk has as free kept its hold (review of #1251).
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(10_000)]
    public void TheChainReset_ClearsAnUnnamedChunk_WhateverItsHeader()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var orphan = Orphan(segment, 4, 4);

        Assert.That(ComponentRevisionManager.ResetChainLocksAfterCrash(segment, _dbe), Is.EqualTo(1), "the unnamed chunk");
        Assert.That(Write(segment, (ref ChunkAccessor<PersistentStore> a) => a.GetChunk<CompRevStorageHeader>(orphan).Control.LockedByThreadId), Is.Zero,
            "its bytes at the lock's offset, cleared");
    }
}
