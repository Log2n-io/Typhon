using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The staged sort of the sorted index rebuild (#1588, design §D3): every key type comes out in the tree's own order, ties by location, raw key bits
/// untouched, and an already-ordered buffer is left alone.
/// </summary>
[TestFixture]
internal sealed class StagedSortTests
{
    private const int Count = 5_000;

    [Test] public void Int_SortsInTreeOrder_TiesByLocation() => Check<int>(r => r.Next(-50, 50));
    [Test] public void Long_SortsInTreeOrder_TiesByLocation() => Check<long>(r => r.NextInt64(-1_000_000_000_000, 1_000_000_000_000) / 1_000_000_000);
    [Test] public void UInt_SortsInTreeOrder_TiesByLocation() => Check<uint>(r => (uint)r.Next() % 200 * 21_000_000u);
    [Test] public void Short_SortsInTreeOrder_TiesByLocation() => Check<short>(r => (short)r.Next(short.MinValue, short.MaxValue));
    [Test] public void Byte_SortsInTreeOrder_TiesByLocation() => Check<byte>(r => (byte)r.Next(256));
    [Test] public void Char_SortsInTreeOrder_TiesByLocation() => Check<char>(r => (char)r.Next(0xD7FF));

    [Test]
    public void Float_SortsInTreeOrder_NaNFirst_ZerosEqual() =>
        Check<float>(r => r.Next(10) switch
        {
            0 => float.NaN,
            1 => BitConverter.Int32BitsToSingle(unchecked((int)0xFFC0_0001)),   // a negative NaN with a payload
            2 => -0.0f,
            3 => 0.0f,
            4 => float.NegativeInfinity,
            5 => float.PositiveInfinity,
            _ => (float)(r.NextDouble() * 200 - 100),
        });

    [Test]
    public void Double_SortsInTreeOrder_NaNFirst_ZerosEqual() =>
        Check<double>(r => r.Next(10) switch
        {
            0 => double.NaN,
            1 => BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0007)),
            2 => -0.0,
            3 => 0.0,
            4 => double.NegativeInfinity,
            5 => double.MaxValue,
            _ => r.NextDouble() * 2e9 - 1e9,
        });

    [Test]
    public void String64_SortsByUnsignedBytes_TiesByLocation() =>
        Check<String64>(r =>
        {
            // High bytes (≥ 0x80) must sort above ASCII: the compare is unsigned.
            var s = new String64();
            var bytes = new byte[64];
            var len = r.Next(1, 4);
            for (var i = 0; i < len; i++)
            {
                bytes[i] = (byte)(r.Next(3) == 0 ? 0xC3 : 'a' + r.Next(3));
            }

            unsafe
            {
                fixed (byte* p = bytes)
                {
                    s = new String64(p, 64);
                }
            }

            return s;
        });

    [Test]
    public void AllEqualKeys_StayInLocationOrder() => Check<int>(_ => 7);

    [Test]
    public void AnAscendingBuffer_IsNotSorted()
    {
        var entries = new StagedEntry<long>[Count];
        for (var i = 0; i < Count; i++)
        {
            entries[i] = new StagedEntry<long>(i / 3, i);
        }

        var before = entries.ToArray();
        var sorted = StagedOrder<long>.Sort(entries, new StagedEntry<long>[Count], new int[RadixSort.Buckets]);
        Assert.Multiple(() =>
        {
            Assert.That(sorted, Is.False, "an ordered buffer skips the sort");
            Assert.That(entries, Is.EqualTo(before));
        });
    }

    [Test]
    public void OneDescent_IsSorted()
    {
        var entries = new StagedEntry<long>[Count];
        for (var i = 0; i < Count; i++)
        {
            entries[i] = new StagedEntry<long>(i, i);
        }

        entries[Count / 2].Key = -1;
        Assert.That(StagedOrder<long>.Sort(entries, new StagedEntry<long>[Count], new int[RadixSort.Buckets]), Is.True);
        Assert.That(entries[0].Key, Is.EqualTo(-1L));
    }

    private static void Check<TKey>(Func<Random, TKey> next) where TKey : unmanaged
    {
        var random = new Random(1588);
        var entries = new StagedEntry<TKey>[Count];
        for (var i = 0; i < Count; i++)
        {
            entries[i] = new StagedEntry<TKey>(next(random), i * 3);   // the stage appends in ascending location order
        }

        var bitsByLocation = entries.ToDictionary(e => e.Location, e => RawBits(e.Key));
        var expected = entries.OrderBy(e => e.Key, Comparer<TKey>.Default).ThenBy(e => e.Location).Select(e => e.Location).ToArray();

        StagedOrder<TKey>.Sort(entries, new StagedEntry<TKey>[Count], new int[RadixSort.Buckets]);

        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(e => e.Location).ToArray(), Is.EqualTo(expected), "the tree's key order, then location");
            Assert.That(entries.All(e => bitsByLocation[e.Location].SequenceEqual(RawBits(e.Key))), Is.True, "every key keeps its raw bits");
            Assert.That(StagedOrder<TKey>.IsOrdered(entries), Is.True);
        });
    }

    private static byte[] RawBits<TKey>(TKey key) where TKey : unmanaged
    {
        var bytes = new byte[Unsafe.SizeOf<TKey>()];
        Unsafe.WriteUnaligned(ref bytes[0], key);
        return bytes;
    }
}
