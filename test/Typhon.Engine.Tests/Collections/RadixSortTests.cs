using System;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="RadixSort"/>, the stable LSD radix sort the fence's hot-path sorts share. Its contract is what every site relies on — ascending by the key
/// struct's key, stable, result in <c>items</c>, nothing allocated — and these pin it against a reference that has the same properties (LINQ's
/// <c>OrderBy</c> is documented stable), across both digit widths, both pass parities, and keys that exercise the skipped-digit paths.
/// </summary>
[TestFixture]
public class RadixSortTests
{
    private struct Item
    {
        public long Key;
        public int Seq;
    }

    private readonly struct ByKey32 : IRadixKey<Item>
    {
        public static ulong Key(in Item item) => RadixSort.SignedKey((int)item.Key);
    }

    private readonly struct ByKey64 : IRadixKey<Item>
    {
        public static ulong Key(in Item item) => RadixSort.SignedKey(item.Key);
    }

    private readonly struct BySeq : IRadixKey<Item>
    {
        public static ulong Key(in Item item) => RadixSort.SignedKey(item.Seq);
    }

    private static Item[] Build(Random rng, int count, Func<Random, long> key)
    {
        var items = new Item[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = new Item { Key = key(rng), Seq = i };
        }

        return items;
    }

    /// <summary>One comparison per element and a message built only on failure — 40 000-item cases would otherwise spend their time formatting
    /// strings.</summary>
    private static void AssertSameSequence(Item[] actual, Item[] expected, string what)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i].Key != expected[i].Key)
            {
                Assert.Fail($"{what}: key at {i} is {actual[i].Key}, expected {expected[i].Key}");
            }

            if (actual[i].Seq != expected[i].Seq)
            {
                Assert.Fail($"{what}: a stable sort keeps the input order inside key {expected[i].Key}; position {i} holds seq {actual[i].Seq}, "
                    + $"expected {expected[i].Seq}");
            }
        }
    }

    private static void Sort64(Item[] items)
    {
        var scratch = new Item[items.Length];
        var counts = new int[RadixSort.Buckets];
        RadixSort.Sort<Item, ByKey64>(items, scratch, counts);
    }

    /// <summary>
    /// Counts on both sides of <see cref="RadixSort.WideDigitMinCount"/> — 8-bit digits below it, 11-bit above — against key ranges that take one, two,
    /// three and six passes, so every pass count and both ping-pong parities are covered under each digit width.
    /// </summary>
    [TestCase(50, 1L << 8, TestName = "Narrow_OnePass")]
    [TestCase(500, 1L << 16, TestName = "Narrow_TwoPasses")]
    [TestCase(1_000, 1L << 20, TestName = "Narrow_ThreePasses")]
    [TestCase(1_023, 1L << 40, TestName = "Narrow_FivePasses_JustBelowTheWideThreshold")]
    [TestCase(1_024, 1L << 11, TestName = "Wide_OnePass_AtTheThreshold")]
    [TestCase(5_000, 1L << 20, TestName = "Wide_TwoPasses")]
    [TestCase(5_000, 1L << 31, TestName = "Wide_ThreePasses_FullPositiveInt")]
    [TestCase(40_000, 1L << 62, TestName = "Wide_SixPasses")]
    public void Sorts_Stably_ByKey(int count, long keyRange)
    {
        var rng = new Random(count ^ (int)keyRange);
        var items = Build(rng, count, r => r.NextInt64(keyRange));
        var expected = items.OrderBy(i => i.Key).ToArray();

        Sort64(items);

        AssertSameSequence(items, expected, $"count {count}, range {keyRange}");
    }

    /// <summary>Through the 32-bit key path, negatives included: the sign flip is what makes the unsigned radix order the <see cref="int"/> order.</summary>
    [TestCase(300)]
    [TestCase(3_000)]
    public void Signed32_Keys_Order_As_Int_CompareTo(int count)
    {
        var rng = new Random(32 + count);
        var items = Build(rng, count, r => r.Next(int.MinValue, int.MaxValue));
        var expected = items.OrderBy(i => (int)i.Key).ToArray();

        var scratch = new Item[count];
        RadixSort.Sort<Item, ByKey32>(items, scratch, new int[RadixSort.Buckets]);

        AssertSameSequence(items, expected, "signed 32");
        Assert.That(items[0].Key, Is.LessThan(0), "sanity: the seed produced negative keys and they came first");
    }

    [TestCase(300)]
    [TestCase(3_000)]
    public void Signed64_Keys_Order_As_Long_CompareTo(int count)
    {
        var rng = new Random(64 + count);
        var items = Build(rng, count, r => r.NextInt64(long.MinValue, long.MaxValue));
        var expected = items.OrderBy(i => i.Key).ToArray();

        Sort64(items);

        AssertSameSequence(items, expected, "signed 64");
        Assert.That(items[0].Key, Is.LessThan(0), "sanity: the seed produced negative keys and they came first");
    }

    /// <summary>
    /// Stability, witnessed where it matters: eight values per digit over three digits give 512 distinct keys for 4 000 items, so nearly every key is shared
    /// and every pass has ties to keep in order. The range cases prove ORDER; this one proves the input order survives inside each key.
    /// </summary>
    [TestCase(900)]
    [TestCase(4_000)]
    public void Colliding_Keys_Keep_Their_Input_Order_Through_Every_Pass(int count)
    {
        var rng = new Random(512);
        var items = Build(rng, count, r => (r.Next(8) << 22) | (r.Next(8) << 11) | r.Next(8));
        var expected = items.OrderBy(i => i.Key).ToArray();

        Sort64(items);

        AssertSameSequence(items, expected, "colliding keys");
    }

    /// <summary>All keys equal: already sorted, and stable means neither the items nor the scratch are touched.</summary>
    [Test]
    public void Equal_Keys_Are_A_NoOp()
    {
        var items = Build(new Random(1), 500, _ => 4242);
        var snapshot = (Item[])items.Clone();
        var scratch = new Item[500];
        scratch.AsSpan().Fill(new Item { Key = -1, Seq = -1 });

        RadixSort.Sort<Item, ByKey64>(items, scratch, new int[RadixSort.Buckets]);

        Assert.That(items.Select(i => i.Seq), Is.EqualTo(snapshot.Select(i => i.Seq)), "the items are exactly as they were");
        Assert.That(scratch.All(s => s.Key == -1 && s.Seq == -1), "the scratch was never written");
    }

    /// <summary>Keys sharing their LOW digits: those passes are skipped without a histogram, and the order is still right.</summary>
    [TestCase(700)]
    [TestCase(3_000)]
    public void Low_Constant_Digits_Are_Skipped_Without_Losing_The_Order(int count)
    {
        var rng = new Random(3);
        var items = Build(rng, count, r => (long)r.Next(1 << 12) << 24 | 0x00AB_CDEF);   // low 24 bits identical, only the top varies
        var expected = items.OrderBy(i => i.Key).ToArray();

        Sort64(items);

        AssertSameSequence(items, expected, "low-constant");
    }

    /// <summary>
    /// Review's case: the two extremes share their whole low digit (0 and 0x200 under 8-bit digits) while keys between them differ there. A skip derived from
    /// <c>min ^ max</c> dropped the low pass and left 0x101 before 0x100; the OR/AND mask sees the varying bit. The wide-digit twin puts the extremes at 0
    /// and 0x800 with 1 024 items so the 11-bit digit is the one shared.
    /// </summary>
    [Test]
    public void Extremes_Sharing_Their_Low_Digit_Do_Not_Skip_It()
    {
        var narrow = new[] { 0x101L, 0x100L, 0L, 0x200L }.Select((k, i) => new Item { Key = k, Seq = i }).ToArray();
        RadixSort.Sort<Item, ByKey64>(narrow, new Item[4], new int[RadixSort.Buckets]);
        Assert.That(narrow.Select(i => i.Key), Is.EqualTo(new[] { 0L, 0x100L, 0x101L, 0x200L }), "narrow digits");

        var rng = new Random(0x101);
        var wide = Build(rng, 1_024, r => (r.Next(4) << 11) | r.Next(1 << 11));   // varies in the low 11-bit digit and the next
        wide[0].Key = 0;
        wide[1].Key = 0x800L * 3;            // extremes 0 and 0x1800 share their low 11 bits; the keys between them do not
        var expected = wide.OrderBy(i => i.Key).ToArray();
        Sort64(wide);
        AssertSameSequence(wide, expected, "wide digits");
    }

    /// <summary>
    /// A key span that is a power of two with BOTH ends present — a bucket range 0..256, a cell range 0..2 048 — is exactly the shape whose extremes agree
    /// on every low bit. Under both digit widths, against the stable reference.
    /// </summary>
    [TestCase(700, 8, TestName = "Narrow_0_To_256")]
    [TestCase(700, 16, TestName = "Narrow_0_To_65536")]
    [TestCase(3_000, 11, TestName = "Wide_0_To_2048")]
    [TestCase(3_000, 22, TestName = "Wide_0_To_4M")]
    public void Power_Of_Two_Span_With_Both_Ends_Present(int count, int spanBits)
    {
        var rng = new Random(count + spanBits);
        var top = 1L << spanBits;
        var items = Build(rng, count, r => r.NextInt64(top + 1));
        items[0].Key = 0;
        items[count - 1].Key = top;
        var expected = items.OrderBy(i => i.Key).ToArray();

        Sort64(items);

        AssertSameSequence(items, expected, $"span 2^{spanBits}");
    }

    /// <summary>Many seeds, random counts and key ranges, against the stable reference — the net under the hand-picked cases above.</summary>
    [Test]
    public void Seed_Sweep_Matches_The_Stable_Reference()
    {
        for (var seed = 0; seed < 120; seed++)
        {
            var rng = new Random(seed);
            var count = rng.Next(2, 1_500);
            var rangeBits = rng.Next(1, 41);
            var items = Build(rng, count, r => r.NextInt64(1L << rangeBits));
            var expected = items.OrderBy(i => i.Key).ToArray();

            Sort64(items);

            for (var i = 0; i < count; i++)
            {
                if (items[i].Key != expected[i].Key || items[i].Seq != expected[i].Seq)
                {
                    Assert.Fail($"seed {seed}, count {count}, {rangeBits} bits: mismatch at {i}");
                }
            }
        }
    }

    /// <summary>Keys sharing a MIDDLE digit: its pass is skipped outright — the varying-bit mask has nothing in that digit — and the order is still
    /// right.</summary>
    [TestCase(700)]
    [TestCase(3_000)]
    public void Middle_Constant_Digits_Are_Skipped_Without_Losing_The_Order(int count)
    {
        var rng = new Random(5);
        // Low digit varies, the next 16 bits are fixed, the high digit varies: under both widths at least one whole middle digit is constant.
        var items = Build(rng, count, r => ((long)r.Next(1 << 8) << 24) | 0x0055_5500 | (uint)r.Next(1 << 8));
        var expected = items.OrderBy(i => i.Key).ToArray();

        Sort64(items);

        AssertSameSequence(items, expected, "middle-constant");
    }

    /// <summary>Two stable passes — minor key first, major key second — give the lexicographic (major, minor) order, which is how a site sorts on two
    /// fields.</summary>
    [Test]
    public void Two_Stable_Passes_Give_The_Lexicographic_Order()
    {
        var rng = new Random(2);
        var items = Build(rng, 2_000, r => r.Next(1 << 6));
        for (var i = 0; i < items.Length; i++)
        {
            items[i].Seq = rng.Next(1 << 10);   // no longer unique: the minor key has ties of its own
        }

        var expected = items.OrderBy(i => i.Key).ThenBy(i => i.Seq).ToArray();
        var scratch = new Item[items.Length];
        var counts = new int[RadixSort.Buckets];

        RadixSort.Sort<Item, BySeq>(items, scratch, counts);
        RadixSort.Sort<Item, ByKey64>(items, scratch, counts);

        for (var i = 0; i < items.Length; i++)
        {
            Assert.That((items[i].Key, items[i].Seq), Is.EqualTo((expected[i].Key, expected[i].Seq)), $"position {i}");
        }
    }

    /// <summary>Only the span is the sort's to touch: elements of the backing array past it stay where they were.</summary>
    [Test]
    public void Elements_Past_The_Span_Are_Not_Touched()
    {
        var rng = new Random(7);
        var items = Build(rng, 1_500, r => r.Next(1 << 20));
        var tail = items[1_200..].ToArray();
        var expected = items.Take(1_200).OrderBy(i => i.Key).ToArray();

        RadixSort.Sort<Item, ByKey64>(items.AsSpan(0, 1_200), new Item[1_200], new int[RadixSort.Buckets]);

        AssertSameSequence(items.Take(1_200).ToArray(), expected, "the prefix");
        for (var i = 0; i < tail.Length; i++)
        {
            Assert.That((items[1_200 + i].Key, items[1_200 + i].Seq), Is.EqualTo((tail[i].Key, tail[i].Seq)), $"entry {1_200 + i}, past the span");
        }
    }

    [Test]
    public void Empty_And_Single_Item_Spans_Are_NoOps()
    {
        var one = new[] { new Item { Key = 9, Seq = 0 } };
        Assert.DoesNotThrow(() => RadixSort.Sort<Item, ByKey64>(Span<Item>.Empty, Span<Item>.Empty, Span<int>.Empty));
        Assert.DoesNotThrow(() => RadixSort.Sort<Item, ByKey64>(one, Span<Item>.Empty, Span<int>.Empty), "one item needs no scratch at all");
        Assert.That(one[0].Key, Is.EqualTo(9));
    }

    /// <summary>The scratch contract is checked, not trusted: a short ping-pong partner would have the scatter write past it.</summary>
    [Test]
    public void Short_Scratch_Or_Short_Counts_Throw()
    {
        var items = Build(new Random(9), 100, r => r.Next(1 << 20));
        Assert.Throws<ArgumentException>(() => RadixSort.Sort<Item, ByKey64>(items, new Item[99], new int[RadixSort.Buckets]));
        Assert.Throws<ArgumentException>(() => RadixSort.Sort<Item, ByKey64>(items, new Item[100], new int[RadixSort.Buckets - 1]));
    }

    /// <summary>The scratch may be longer than the items and the counts longer than the buckets; only the prefixes are used.</summary>
    [Test]
    public void Oversized_Scratch_Is_Fine()
    {
        var rng = new Random(11);
        var items = Build(rng, 1_500, r => r.Next(1 << 20));
        var expected = items.OrderBy(i => i.Key).ToArray();

        RadixSort.Sort<Item, ByKey64>(items, new Item[4_000], new int[RadixSort.Buckets * 2]);

        AssertSameSequence(items, expected, "oversized scratch");
    }
}
