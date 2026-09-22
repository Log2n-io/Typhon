using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>BTree&lt;TKey, TStore&gt;.RadixKeyOf</c>: the order-preserving map from a tree's key to the unsigned radix key its staged runs are sorted by. The
/// partitioning descent asserts the batch against <c>CompareKeys</c>, so the map must agree with <c>Comparer&lt;TKey&gt;.Default</c> on every pair — the
/// integer widths, both signs, and the floating-point specials (±0, ±∞, NaN) where the zone-map encoding it borrows does not agree on its own.
/// </summary>
[TestFixture]
public class BTreeRadixKeyTests
{
    private static void AssertMonotone<TKey>(TKey[] values) where TKey : unmanaged
    {
        var comparer = Comparer<TKey>.Default;
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < values.Length; j++)
            {
                var expected = Math.Sign(comparer.Compare(values[i], values[j]));
                var actual = BTree<TKey, PersistentStore>.RadixKeyOf(values[i]).CompareTo(BTree<TKey, PersistentStore>.RadixKeyOf(values[j]));
                Assert.That(Math.Sign(actual), Is.EqualTo(expected),
                    $"{typeof(TKey).Name}: {values[i]} vs {values[j]} — CompareTo says {expected}, the radix keys say {Math.Sign(actual)}");
            }
        }
    }

    [Test]
    public void Signed_Integers_Order_As_CompareTo()
    {
        AssertMonotone<sbyte>([sbyte.MinValue, -1, 0, 1, sbyte.MaxValue, -100, 100]);
        AssertMonotone<short>([short.MinValue, -1, 0, 1, short.MaxValue, -1000, 1000]);
        AssertMonotone<int>([int.MinValue, -1, 0, 1, int.MaxValue, -100_000, 100_000, 1 << 20]);
        AssertMonotone<long>([long.MinValue, -1, 0, 1, long.MaxValue, -1L << 40, 1L << 40, 1L << 62]);
    }

    [Test]
    public void Unsigned_Integers_Order_As_CompareTo()
    {
        AssertMonotone<byte>([0, 1, 127, 128, 255]);
        AssertMonotone<ushort>([0, 1, 32767, 32768, 65535]);
        AssertMonotone<uint>([0, 1, int.MaxValue, 1u << 31, uint.MaxValue]);
        AssertMonotone<ulong>([0, 1, long.MaxValue, 1UL << 63, ulong.MaxValue]);
    }

    /// <summary>NaN below everything and every NaN equal; -0.0 equal to +0.0; the infinities at the ends — <see cref="float.CompareTo(float)"/>'s
    /// order.</summary>
    [Test]
    public void Float_Orders_As_CompareTo_Specials_Included()
    {
        var negativeNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC0_0000));
        AssertMonotone<float>([float.NaN, negativeNaN, float.NegativeInfinity, float.MinValue, -1e30f, -1f, -float.Epsilon, -0f, 0f, float.Epsilon, 1f,
            1e30f, float.MaxValue, float.PositiveInfinity]);
    }

    [Test]
    public void Double_Orders_As_CompareTo_Specials_Included()
    {
        var negativeNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0000));
        AssertMonotone<double>([double.NaN, negativeNaN, double.NegativeInfinity, double.MinValue, -1e300, -1d, -double.Epsilon, -0d, 0d, double.Epsilon, 1d,
            1e300, double.MaxValue, double.PositiveInfinity]);
    }

    /// <summary>Random draws over the whole range of each type, against the same reference — the specials above are the edges, this is the bulk.</summary>
    [Test]
    public void Random_Keys_Order_As_CompareTo()
    {
        var rng = new Random(0xBEEF);
        var ints = new int[64];
        var longs = new long[64];
        var floats = new float[64];
        var doubles = new double[64];
        for (var i = 0; i < 64; i++)
        {
            ints[i] = rng.Next(int.MinValue, int.MaxValue);
            longs[i] = rng.NextInt64(long.MinValue, long.MaxValue);
            floats[i] = BitConverter.Int32BitsToSingle(rng.Next(int.MinValue, int.MaxValue));
            doubles[i] = BitConverter.Int64BitsToDouble(rng.NextInt64(long.MinValue, long.MaxValue));
        }

        AssertMonotone(ints);
        AssertMonotone(longs);
        AssertMonotone(floats);
        AssertMonotone(doubles);
    }
}
