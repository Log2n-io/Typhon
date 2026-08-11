using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// What revision chains actually look like at rest — measured, so the <c>CHN</c> checks assert the truth rather than the
/// catalogue's shorthand.
/// </summary>
/// <remarks>
/// <para>
/// <c>03-checks.md</c> lists <c>CHN-02</c> as <i>"post-recovery chains satisfy <c>ItemCount == 1</c>,
/// <c>NextChunkId == 0</c>, <c>UowId</c> cleared"</i>, citing <c>RB-03</c>. But <c>RB-03</c> is the postcondition of the
/// <b>chain scrub</b>, and the scrub runs on the <b>crash path</b> — so a cleanly-closed database that has simply
/// accumulated MVCC history has every right to hold multi-element chains. An offline scanner reading a file at rest
/// cannot tell "never scrubbed because it never crashed" from "crashed and the scrub did not run", and a check that
/// conflated them would report a divergence on every healthy database with history in it.
/// </para>
/// <para>
/// Rather than reason about which it is, this measures it: build a database that deliberately generates history, close
/// it cleanly, and read the chain headers out of the file. The numbers decide whether <c>CHN-02</c> can be
/// unconditional, must be gated on the clean-shutdown flag, or is simply not decidable offline — and that answer belongs
/// in the design before the check is written, not after.
/// </para>
/// <para>
/// It asserts nothing about the shape it finds on purpose. What it does assert is that it <i>found</i> chains at all: a
/// probe that measured an empty set and reported "all chains are single-element" would be the worst possible input to
/// that decision.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ChainShapeAtRestTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void WhatRevisionChainsLookLikeAfterACleanCloseWithHistory()
    {
        var descriptors = new List<StorageSegmentDescriptor>();

        using (var scope = Provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            var ids = new List<EntityId>();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 32; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    ids.Add(tx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
                    tx.Commit();
                }

                uow.Flush();
            }

            // Update each entity several times. This is what puts more than one element in a chain — a spawn alone
            // leaves a chain of length one, and a probe built only from spawns would answer its own question wrongly.
            // A fresh UnitOfWork per round: Flush moves a UoW to WalDurable, after which it issues no more transactions.
            for (var round = 0; round < 4; round++)
            {
                using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
                for (var i = 0; i < ids.Count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    ref var w = ref tx.OpenMut(ids[i]).Write(CompAArch.A);
                    w.B = round + 1;
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();

            foreach (var seg in dbe.EnumerateStorageSegments())
            {
                if (seg.Stride > 0 && seg.Kind == StorageSegmentKind.Revision)
                {
                    descriptors.Add(seg);
                }
            }
        }

        CloseEngine();

        var file = File.ReadAllBytes(DamageKit.DataPath(BundlePath));
        var report = new List<string>();
        var chainsSeen = 0;
        var multiElement = 0;
        var withNext = 0;
        var itemCounts = new SortedDictionary<int, int>();

        foreach (var seg in descriptors)
        {
            var pages = seg.Pages.Span;
            var geometry = ChunkGeometry.FromPage(file.AsSpan(pages[0] * IntegrityConstants.PageSize, IntegrityConstants.PageSize));
            if (!geometry.IsUsable)
            {
                report.Add($"segment @{seg.RootPageIndex}: no usable stride");
                continue;
            }

            for (var id = 0; id < seg.ChunkCapacity; id++)
            {
                if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= pages.Length)
                {
                    continue;
                }

                var page = file.AsSpan(pages[ordinal] * IntegrityConstants.PageSize, IntegrityConstants.PageSize);
                if (!geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
                {
                    continue;
                }

                var chunk = page.Slice(geometry.OffsetInPage(ordinal, chunkInPage), geometry.Stride);
                var header = MemoryMarshal.Read<CompRevStorageHeader>(chunk);

                // A chain ROOT is a chunk an EntityMap points at; without the map, "has a plausible EntityPK" is the
                // best available filter and is stated as such rather than dressed up as a classification.
                if (header.EntityPK == 0)
                {
                    continue;
                }

                chainsSeen++;
                itemCounts.TryGetValue(header.ItemCount, out var n);
                itemCounts[header.ItemCount] = n + 1;

                if (header.ItemCount > 1)
                {
                    multiElement++;
                }

                if (header.NextChunkId != 0)
                {
                    withNext++;
                }
            }
        }

        report.Insert(0, $"sizeof(CompRevStorageHeader) = {Marshal.SizeOf<CompRevStorageHeader>()}");
        report.Insert(1, $"revision segments = {descriptors.Count}");
        report.Insert(2, $"chunks with a non-zero EntityPK = {chainsSeen}");
        report.Insert(3, $"  ItemCount > 1 = {multiElement}");
        report.Insert(4, $"  NextChunkId != 0 = {withNext}");
        foreach (var kv in itemCounts)
        {
            report.Add($"  ItemCount={kv.Key}: {kv.Value} chunk(s)");
        }

        var measured = string.Join("\n  ", report);

        // A probe that found nothing would answer the design question with silence, which reads exactly like "every
        // chain is already consolidated".
        Assert.That(chainsSeen, Is.GreaterThan(0), "no chain roots were found at all, so nothing was measured:\n  " + measured);
        Assert.That(chainsSeen, Is.EqualTo(32), "one chain root per spawned entity was expected:\n  " + measured);

        // THE MEASURED FACT, and the precondition CHN-02 will rest on: a consolidating checkpoint collapses every chain
        // to its head. 32 entities updated four times each — 128 revisions written — and not one chain still holds
        // history at rest. So the shape RB-03 describes as the SCRUB's postcondition is also what an ordinary
        // checkpoint leaves behind, which is why CHN-02 can assert it without knowing whether recovery ever ran.
        //
        // The boundary this does NOT establish, and which CHN-02 must therefore respect: a file closed without a
        // checkpoint, or left behind by a crash, has had no such consolidation. On those the same assertion would report
        // a divergence on a perfectly healthy database, so the check has to gate on the bootstrap's clean-shutdown flag
        // rather than fire unconditionally.
        Assert.That(multiElement, Is.Zero,
            "a checkpointed, cleanly-closed database still holds multi-element revision chains. CHN-02 was designed on "
            + "the measurement below, and this is that measurement changing:\n  " + measured);
        Assert.That(withNext, Is.Zero,
            "a chain root still points at a continuation chunk after a checkpoint:\n  " + measured);

        // Pinned because the offline reader slices chunks by it. A silent change to the header's layout would move every
        // element read in the CHN family by the delta, and nothing else in the suite would notice.
        Assert.That(Marshal.SizeOf<CompRevStorageHeader>(), Is.EqualTo(28),
            "CompRevStorageHeader changed size; every CHN check's element arithmetic moves with it");
    }
}
