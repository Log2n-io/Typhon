using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── #676: the suite had no ulong-typed indexed field at all, which is the whole reason a signed-ordered ULong tree survived. ──

[Component("Typhon.Test.UlongIdx.Account", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct UlongIdxAccount
{
    [Index]
    public ulong Handle;

    public int Tag;
}

[Archetype]
partial class UlongIdxArch : Archetype<UlongIdxArch>
{
    public static readonly Comp<UlongIdxAccount> Account = Register<UlongIdxAccount>();
}

/// <summary>
/// #676 — a <c>ulong</c> secondary index must order its keys UNSIGNED, so values at or above 2^63 sort after smaller ones rather than before zero.
/// </summary>
/// <remarks>
/// <para>
/// The index was declared <c>L64BTree&lt;long&gt;</c>, so the keys were stored reinterpreted as signed. Two consequences: anything ≥ 2^63 sorted before 0,
/// and the full range <c>[0, ulong.MaxValue]</c> read signed as <c>[0, -1]</c> — empty. <c>KeyRange.IsStreamable</c> excluded <c>ULong</c> to keep queries off
/// that path, so unordered queries fell to the SoA scan and stayed correct; ordered ones returned nothing.
/// </para>
/// <para>
/// Every value here is chosen to straddle the sign boundary: <c>2^63</c> and <c>ulong.MaxValue</c> are both negative read as <c>long</c>, so any signed
/// comparison anywhere in the stack puts them before the small ones and this fixture fails. A test using only small handles would pass against the old code.
/// </para>
/// </remarks>
class UlongIndexOrderingTests : TestBase<UlongIndexOrderingTests>
{
    // Ascending as UNSIGNED. As signed longs the last two are negative, so a signed order would place them first.
    private static readonly ulong[] Handles =
    [
        0UL,
        1UL,
        42UL,
        (ulong)long.MaxValue,          // 2^63 - 1  → still positive signed
        9223372036854775808UL,         // 2^63      → long.MinValue signed
        ulong.MaxValue,                // 2^64 - 1  → -1 signed
    ];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<UlongIdxAccount>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Spawn(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Handles.Length; i++)
        {
            var a = new UlongIdxAccount { Handle = Handles[i], Tag = i };
            tx.Spawn<UlongIdxArch>(UlongIdxArch.Account.Set(in a));
        }
        tx.Commit();
    }

    [Test]
    public void OrderedQuery_OnAUlongIndex_ReturnsEveryRowInUnsignedOrder()
    {
        using var dbe = SetupEngine();
        Spawn(dbe);
        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();
        var got = new List<ulong>();
        // The full-range WhereField is required plumbing, not part of the claim: OrderByField needs a preceding WhereField to identify the component table
        // (#706's second shape). The predicate admits every row, so what is under test is purely the ORDER.
        foreach (var id in tx.Query<UlongIdxArch>()
                     .WhereField<UlongIdxAccount>(a => a.Handle >= ulong.MinValue)
                     .OrderByField<UlongIdxAccount, ulong>(a => a.Handle)
                     .ExecuteOrdered())
        {
            got.Add(tx.Open(id).Read(UlongIdxArch.Account).Handle);
        }

        Assert.That(got, Is.EqualTo(Handles).AsCollection,
            "an ordered scan of a ulong index must be ascending UNSIGNED — a signed tree puts 2^63 and ulong.MaxValue first, or drops every row");
    }

    [Test]
    public void RangeQuery_AcrossTheSignBoundary_ReturnsTheHighHandles()
    {
        using var dbe = SetupEngine();
        Spawn(dbe);
        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();

        // Everything at or above 2^63 — exactly the two values a signed order treats as smaller than zero.
        var high = tx.Query<UlongIdxArch>().WhereField<UlongIdxAccount>(a => a.Handle >= 9223372036854775808UL).Count();
        Assert.That(high, Is.EqualTo(2), "a >= predicate at the sign boundary must match the two high handles, not zero rows and not everything");

        var low = tx.Query<UlongIdxArch>().WhereField<UlongIdxAccount>(a => a.Handle < 9223372036854775808UL).Count();
        Assert.That(low, Is.EqualTo(4), "the complement must be the four handles below 2^63");
    }

    [Test]
    public void EqualityQuery_OnAHandleAboveTheSignBoundary_FindsIt()
    {
        using var dbe = SetupEngine();
        Spawn(dbe);
        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(tx.Query<UlongIdxArch>().WhereField<UlongIdxAccount>(a => a.Handle == ulong.MaxValue).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<UlongIdxArch>().WhereField<UlongIdxAccount>(a => a.Handle == 9223372036854775808UL).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<UlongIdxArch>().WhereField<UlongIdxAccount>(a => a.Handle == 0UL).Count(), Is.EqualTo(1));
        });
    }
}
