using NUnit.Framework;
using System;
using System.Buffers.Binary;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The source/codec pairing table (design/Subscriptions/13 § 2.3, E-2, E-3): exact by default, lossy only when declared, never lossy for no gain.
/// </summary>
[TestFixture]
public class CodecPairingTests
{
    private enum SmallEnum
    {
        A,
        B,
        C,
        D,
        E,
        F,
    }

    private enum WideEnum
    {
        A,
        Nine = 9,
    }

    private static ColumnPath Classify(Type source, Codec codec, bool message = false) =>
        CodecPairing.Classify(source, codec.Catalog, codec.Saturating, "Field 'f'", message);

    [TestCase(typeof(byte), "u8")]
    [TestCase(typeof(sbyte), "i8")]
    [TestCase(typeof(ushort), "u16")]
    [TestCase(typeof(short), "i16")]
    [TestCase(typeof(int), "i32")]
    [TestCase(typeof(int), "vari")]
    [TestCase(typeof(uint), "u32")]
    [TestCase(typeof(uint), "varu")]
    [TestCase(typeof(bool), "bool")]
    [TestCase(typeof(byte), "u16")]
    [TestCase(typeof(ushort), "i32")]
    [TestCase(typeof(byte), "vari")]
    public void AnIntegerInACodecThatCoversItIsExact(Type source, string token)
    {
        Assert.That(Classify(source, CodecFor(token)), Is.EqualTo(ColumnPath.ExactInteger));
    }

    [Test]
    public void AFloatInF32CopiesItsBitsAndEveryOtherFloatCodecQuantizes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(float), Codec.F32), Is.EqualTo(ColumnPath.ExactSingle));
            Assert.That(Classify(typeof(float), Codec.F16), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(float), Codec.Quant(-1, 1, 16)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(float), Codec.Unorm(8)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(double), Codec.F32), Is.EqualTo(ColumnPath.Quantizing), "a double in f32 is a declared narrowing, W1's");
            Assert.That(Classify(typeof(double), Codec.Angle(16)), Is.EqualTo(ColumnPath.Quantizing));
        });
    }

    [TestCase(typeof(int), "u8")]
    [TestCase(typeof(int), "varu")]
    [TestCase(typeof(uint), "i32")]
    [TestCase(typeof(uint), "u16")]
    [TestCase(typeof(long), "varu")]
    [TestCase(typeof(ulong), "u32")]
    [TestCase(typeof(byte), "bool")]
    [TestCase(typeof(short), "u16")]
    public void ANarrowingOfAnyWidthNeedsSaturate(Type source, string token)
    {
        var codec = CodecFor(token);
        var refused = Assert.Throws<InvalidOperationException>(() => Classify(source, codec));
        Assert.Multiple(() =>
        {
            Assert.That(refused.Message, Does.Contain(source.Name), "the refusal names the source type");
            Assert.That(refused.Message, Does.Contain(token), "and the codec");
            Assert.That(refused.Message, Does.Contain(".Saturate()"), "and the explicit way to narrow");
            Assert.That(Classify(source, codec.Saturate()), Is.EqualTo(ColumnPath.NarrowingInteger), "declared, it clamps and counts");
        });
    }

    [TestCase(typeof(float), "u8")]
    [TestCase(typeof(float), "u32")]
    [TestCase(typeof(double), "varu")]
    [TestCase(typeof(float), "vari")]
    public void AFloatInAnIntegerCodecIsRefusedEvenWhenSaturated(Type source, string token)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Classify(source, CodecFor(token).Saturate()));
        Assert.That(refused.Message, Does.Contain("Codec.Quant"), "the refusal names the codec that says the same thing with a stated range");
    }

    [Test]
    public void AnIntegerInAFloatCodecIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(int), Codec.F32));
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(short), Codec.F16));
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(int), Codec.Unorm(8)));
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(int), Codec.Angle(16)));
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(bool), Codec.F32));
        });
    }

    /// <summary>A quant over an integer's range is a declared bandwidth trade, like any quantization — 0..1000 in 8 bits.</summary>
    [Test]
    public void AnIntegerMayTravelAsAQuant()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(int), Codec.Quant(0, 1000, 8)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(short), Codec.Quant(-500, 500, 16)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(long), Codec.Quant(0, 1e12, 32)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(int), Codec.Quant(0, 1000, 8), message: true), Is.EqualTo(ColumnPath.Quantizing), "on a command or an event too");
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(bool), Codec.Quant(0, 1, 8)), "a bool is one bit; a quant cannot do better");
        });
    }

    [Test]
    public void AnEnumWhoseNamesFitNeedsNoSaturateButStillClamps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(SmallEnum), Codec.Bits(3)), Is.EqualTo(ColumnPath.NarrowingInteger),
                "an int-backed enum in bits{3}: its names fit, so no Saturate — and a value cast in from outside them is clamped, not masked");
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(WideEnum), Codec.Bits(3)), "a name of 9 does not fit three bits");
            Assert.That(Classify(typeof(WideEnum), Codec.Bits(3).Saturate()), Is.EqualTo(ColumnPath.NarrowingInteger));
        });
    }

    [Test]
    public void AnIntegerIsAnIdentityOnlyInAMessage()
    {
        Assert.Multiple(() =>
        {
            var refused = Assert.Throws<InvalidOperationException>(() => Classify(typeof(uint), Codec.EntityRef));
            Assert.That(refused.Message, Does.Contain("EntityId"), "an entity's state names an entity by EntityId, which can be resolved (W35)");
            Assert.That(Classify(typeof(uint), Codec.EntityRef, message: true), Is.EqualTo(ColumnPath.None),
                "a command carries the netId its client holds, as a uint (01 § 7)");
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(int), Codec.EntityRef, message: true), "a netId is a uint");
            Assert.That(Classify(typeof(EntityId), Codec.EntityRef), Is.EqualTo(ColumnPath.None), "an EntityId is not this table's to judge");
        });
    }

    [Test]
    public void TickLoTakesATick()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(uint), Codec.TickLo), Is.EqualTo(ColumnPath.ExactInteger));
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(int), Codec.TickLo));
        });
    }

    [Test]
    public void ACodecTheTableDoesNotJudgeIsLeftToItsOwner()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(float), Codec.Vec2(0.01, 16)), Is.EqualTo(ColumnPath.None));
            Assert.That(Classify(typeof(int), Codec.Str(16)), Is.EqualTo(ColumnPath.None));
            Assert.That(Classify(typeof(float), Codec.Pos2), Is.EqualTo(ColumnPath.None));
        });
    }

    /// <summary>The builder runs the table at declaration: a narrowing without <c>Saturate()</c> never reaches <c>Start</c>.</summary>
    [Test]
    public void TheRegistryRefusesANarrowingAtDeclaration()
    {
        var subs = new SubscriptionsRegistry();
        var refused = Assert.Throws<InvalidOperationException>(() => subs.Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjCreature.Ai, x => x.ThinkCooldown, Codec.U8, name: "cooldown")));

        Assert.That(refused.Message, Does.Contain("'cooldown'"), "the refusal names the field");
        Assert.DoesNotThrow(() => new SubscriptionsRegistry().Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjCreature.Ai, x => x.ThinkCooldown, Codec.U8.Saturate(), name: "cooldown")));
    }

    /// <summary>A cast in the selector names the same stored field: the table judges that field's type, so <c>(byte)</c> cannot hide an <c>int</c>.</summary>
    [Test]
    public void ACastInTheSelectorDoesNotHideTheStoredType()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new SubscriptionsRegistry().Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjCreature.Ai, x => (byte)x.ThinkCooldown, Codec.U8, name: "cooldown")));

        Assert.That(refused.Message, Does.Contain("Int32"), "the stored int is judged, not the byte the selector converts it to");
    }

    private static Codec CodecFor(string token) => token switch
    {
        "u8" => Codec.U8,
        "i8" => Codec.I8,
        "u16" => Codec.U16,
        "i16" => Codec.I16,
        "u32" => Codec.U32,
        "i32" => Codec.I32,
        "varu" => Codec.VarUInt,
        "vari" => Codec.VarInt,
        "bool" => Codec.Bool,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null),
    };
}

/// <summary>
/// The exact paths are bit-identical to the binary64 path they replace (13 § 4, E-8): same codes for every value a pairing admits, minus the arithmetic.
/// </summary>
[TestFixture]
public class ColumnPathEquivalenceTests
{
    private const int Slots = 64;

    /// <summary>The code the previous double path produced — round half away from zero, clamp in double, truncate: what the exact path must equal.</summary>
    private static uint Reference(double value, double min, double max)
    {
        var rounded = WireMath.RoundHalfAwayFromZero(WireMath.CanonicalizeNaN(value));
        if (double.IsNaN(rounded))
        {
            return 0;
        }

        var clamped = rounded < min ? min : rounded > max ? max : rounded;
        return unchecked((uint)(long)clamped);
    }

    private static CompiledField Field(ProjectionSourceType source, Codec codec, ColumnPath path, int stride) =>
        new()
        {
            Name = "f",
            ComponentSize = stride,
            FieldOffsetInComponent = 0,
            RatioOffsetInComponent = -1,
            SourceType = source,
            Codec = codec.Catalog,
            CodecKind = codec.Catalog.Kind,
            Path = path,
            IntMin = CodecPairing.ClampRange(codec.Catalog).Min,
            IntMax = CodecPairing.ClampRange(codec.Catalog).Max,
        };

    [TestCase("SByte", 1, "i8")]
    [TestCase("Byte", 1, "u8")]
    [TestCase("Int16", 2, "i16")]
    [TestCase("UInt16", 2, "u16")]
    [TestCase("Int32", 4, "i32")]
    [TestCase("Int32", 4, "vari")]
    [TestCase("UInt32", 4, "u32")]
    [TestCase("UInt32", 4, "varu")]
    public void TheExactIntegerPathEqualsTheDoublePathOnEveryValue(string sourceName, int stride, string token)
    {
        var source = Enum.Parse<ProjectionSourceType>(sourceName);
        var codec = Token(token);
        var (min, max) = CodecPairing.ClampRange(codec.Catalog);
        var rng = new Random(0x1085 + stride);
        var column = new byte[Slots * stride];
        var values = new double[Slots];
        var codes = new ulong[Slots];
        var field = Field(source, codec, ColumnPath.ExactInteger, stride);
        var (clamps, mismatches, first) = (0, 0, (string)null);
        for (var round = 0; round < RoundsFor(source); round++)
        {
            for (var slot = 0; slot < Slots; slot++)
            {
                values[slot] = Write(column, slot, stride, source, rng, IndexFor(source, round, slot));
            }

            clamps += ProjectionColumnWalk.Quantize(field, new ProjectionColumn(column, stride, 0), ulong.MaxValue, codes);
            for (var slot = 0; slot < Slots; slot++)
            {
                if (unchecked((uint)codes[slot]) != Reference(values[slot], min, max))
                {
                    mismatches++;
                    first ??= $"{source} {values[slot]} in {token}: 0x{codes[slot]:X} against 0x{Reference(values[slot], min, max):X}";
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(clamps, Is.Zero, "a covering codec never clamps");
            Assert.That(mismatches, Is.Zero, first);
        });
    }

    [TestCase("Int32", 4, "u8")]
    [TestCase("Int32", 4, "varu")]
    [TestCase("UInt32", 4, "i16")]
    [TestCase("Int16", 2, "u8")]
    [TestCase("Int64", 8, "varu")]
    [TestCase("UInt64", 8, "u32")]
    public void TheNarrowingPathClampsLikeTheDoublePathAndCountsEveryClamp(string sourceName, int stride, string token)
    {
        var source = Enum.Parse<ProjectionSourceType>(sourceName);
        var codec = Token(token);
        var (min, max) = CodecPairing.ClampRange(codec.Catalog);
        var rng = new Random(0x2085 + stride);
        var column = new byte[Slots * stride];
        var values = new double[Slots];
        var codes = new ulong[Slots];
        var field = Field(source, codec, ColumnPath.NarrowingInteger, stride);
        var (clamps, expectedClamps, mismatches, first) = (0, 0, 0, (string)null);
        for (var round = 0; round < RoundsFor(source); round++)
        {
            for (var slot = 0; slot < Slots; slot++)
            {
                values[slot] = Write(column, slot, stride, source, rng, RoundsFor(source) != 64 ? IndexFor(source, round, slot) : -1);
                expectedClamps += values[slot] < min || values[slot] > max ? 1 : 0;
            }

            clamps += ProjectionColumnWalk.Quantize(field, new ProjectionColumn(column, stride, 0), ulong.MaxValue, codes);
            for (var slot = 0; slot < Slots; slot++)
            {
                if (unchecked((uint)codes[slot]) != Reference(values[slot], min, max))
                {
                    mismatches++;
                    first ??= $"{source} {values[slot]} in {token}: 0x{codes[slot]:X} against 0x{Reference(values[slot], min, max):X}";
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(clamps, Is.EqualTo(expectedClamps), "every out-of-range value is counted once");
            Assert.That(mismatches, Is.Zero, first);
        });
    }

    /// <summary>An integer column in a quant takes W1's binary64 path: its codes are <see cref="WireMath.EncodeQuant"/>'s for the widened value.</summary>
    [Test]
    public void AnIntegerQuantColumnEncodesExactlyAsW1()
    {
        const double min = 0;
        const double max = 1000;
        const int bits = 8;
        var codec = Codec.Quant(min, max, bits);
        var field = Field(ProjectionSourceType.Int32, codec, ColumnPath.Quantizing, 4) with { QuantMin = min, QuantMax = max, CodecBits = bits };
        var rng = new Random(0x4085);
        var column = new byte[Slots * 4];
        var values = new int[Slots];
        for (var slot = 0; slot < Slots; slot++)
        {
            values[slot] = slot < 4 ? new[] { -1, 0, 999, 5000 }[slot] : rng.Next(-100, 1100);
            BinaryPrimitives.WriteInt32LittleEndian(column.AsSpan(slot * 4), values[slot]);
        }

        var codes = new ulong[Slots];
        ProjectionColumnWalk.Quantize(field, new ProjectionColumn(column, 4, 0), ulong.MaxValue, codes);

        for (var slot = 0; slot < Slots; slot++)
        {
            Assert.That(codes[slot], Is.EqualTo((ulong)WireMath.EncodeQuant(values[slot], min, max, bits)), $"{values[slot]}");
        }
    }

    /// <summary>
    /// A command's integer field that travelled as a quant decodes between integers and is rounded half away from zero into the field (W1's rha), never
    /// truncated — and a whole number, which every integer codec decodes to, is stored as it is.
    /// </summary>
    [TestCase(3.6, 4)]
    [TestCase(3.5, 4)]
    [TestCase(3.4, 3)]
    [TestCase(-2.5, -3)]
    [TestCase(-2.4, -2)]
    [TestCase(7.0, 7)]
    [TestCase(1e12, int.MaxValue)]
    public void ACommandsIntegerFieldIsRoundedNotTruncated(double decoded, int expected)
    {
        var binding = new CommandFieldBinding("f", 0, 1, CommandFieldElement.I32, 4);
        var payload = new byte[4];
        binding.Store(payload, [decoded]);

        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(payload), Is.EqualTo(expected));
    }

    /// <summary>
    /// A negative value's code is its 64-bit two's complement; every writer truncates it to the codec's width, so the bytes are the typed value's own —
    /// the place the widening from <c>uint</c> codes could have broken a signed codec.
    /// </summary>
    [TestCase(-1)]
    [TestCase(-5)]
    [TestCase(-128)]
    [TestCase(-32768)]
    [TestCase(int.MinValue)]
    [TestCase(127)]
    public void ANegativeCodeWritesTheTypedValuesBytes(int value)
    {
        var code = unchecked((ulong)(long)value);
        var want = new byte[8];

        if (value >= sbyte.MinValue)
        {
            var w8 = new WireWriter(want);
            w8.WriteU8(unchecked((byte)(sbyte)value));
            Assert.That(Bytes(Codec.I8, code), Is.EqualTo(want[..w8.Position]), "i8");
        }

        if (value >= short.MinValue)
        {
            var w16 = new WireWriter(want);
            w16.WriteU16(unchecked((ushort)(short)value));
            Assert.That(Bytes(Codec.I16, code), Is.EqualTo(want[..w16.Position]), "i16");
        }

        var w32 = new WireWriter(want);
        w32.WriteU32(unchecked((uint)value));
        Assert.That(Bytes(Codec.I32, code), Is.EqualTo(want[..w32.Position]), "i32");

        var wv = new WireWriter(want);
        wv.WriteVari(value);
        Assert.That(Bytes(Codec.VarInt, code), Is.EqualTo(want[..wv.Position]), "vari");
    }

    private static byte[] Bytes(Codec codec, ulong code)
    {
        var buffer = new byte[8];
        var writer = new WireWriter(buffer);
        ProjectionPass.WriteCode(ref writer, Field(ProjectionSourceType.Int32, codec, ColumnPath.ExactInteger, 4), code);
        return buffer[..writer.Position];
    }

    [Test]
    public void AUlongAboveTheLongRangeClampsToTheTopRatherThanWrapping()
    {
        var column = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(column, ulong.MaxValue);
        var codes = new ulong[Slots];
        var clamps = ProjectionColumnWalk.Quantize(Field(ProjectionSourceType.UInt64, Codec.U32.Saturate(), ColumnPath.NarrowingInteger, 8),
            new ProjectionColumn(column, 8, 0), 1UL, codes);

        Assert.That(clamps, Is.EqualTo(1));
        Assert.That(codes[0], Is.EqualTo((ulong)uint.MaxValue), "2^64 - 1 is above every 32-bit codec, never below it");
    }

    [Test]
    public void TheExactSinglePathEqualsEncodeSingleOnEveryBitPattern()
    {
        var rng = new Random(0x3085);
        var column = new byte[Slots * 4];
        uint[] specials =
        [
            0x00000000, 0x80000000, 0x7F800000, 0xFF800000, 0x7FC00000, 0xFFC00000, 0x7F800001, 0xFFFFFFFF, 0x7FBFFFFF, 0x00000001, 0x807FFFFF,
            0x7F7FFFFF, 0x3F800000,
        ];

        for (var round = 0; round < 256; round++)
        {
            for (var slot = 0; slot < Slots; slot++)
            {
                var bits = round == 0 && slot < specials.Length ? specials[slot] : (uint)rng.NextInt64(0, 1L << 32);
                BinaryPrimitives.WriteUInt32LittleEndian(column.AsSpan(slot * 4), bits);
            }

            var codes = new ulong[Slots];
            ProjectionColumnWalk.Quantize(Field(ProjectionSourceType.Single, Codec.F32, ColumnPath.ExactSingle, 4), new ProjectionColumn(column, 4, 0),
                ulong.MaxValue, codes);

            for (var slot = 0; slot < Slots; slot++)
            {
                var value = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(column.AsSpan(slot * 4)));
                Assert.That(codes[slot], Is.EqualTo((ulong)WireMath.EncodeSingle(value)), $"bits 0x{BitConverter.SingleToUInt32Bits(value):X8}");
            }
        }
    }

    // How many 64-slot rounds a case runs: every value of an 8- or 16-bit source, exhaustively; 64 rounds of random values (the first one the edges)
    // for the wider ones.
    private static int RoundsFor(ProjectionSourceType source) => source switch
    {
        ProjectionSourceType.SByte or ProjectionSourceType.Byte => 256 / Slots,
        ProjectionSourceType.Int16 or ProjectionSourceType.UInt16 => 65536 / Slots,
        _ => 64,
    };

    // The value index a slot of a round takes: exhaustive for the small sources, an edge on round 0 and random afterwards for the others.
    private static int IndexFor(ProjectionSourceType source, int round, int slot) =>
        RoundsFor(source) != 64 ? (round * Slots) + slot : round == 0 ? slot : -1;

    // Writes a value of the source type at a slot and returns it as a double — exact for every type up to 32 bits; for the 64-bit cases the reference
    // only needs the side of the clamp, which a double keeps. `index` ≥ 0 picks a value (every value, in order, for 8/16 bits; an edge for 32), -1 a
    // random one.
    private static double Write(byte[] column, int slot, int stride, ProjectionSourceType source, Random rng, int index)
    {
        var at = column.AsSpan(slot * stride);
        switch (source)
        {
            case ProjectionSourceType.SByte:
                var s8 = (sbyte)(sbyte.MinValue + (index & 0xFF));
                at[0] = unchecked((byte)s8);
                return s8;
            case ProjectionSourceType.Byte:
                var u8 = (byte)index;
                at[0] = u8;
                return u8;
            case ProjectionSourceType.Int16:
                var s16 = (short)(short.MinValue + (index & 0xFFFF));
                BinaryPrimitives.WriteInt16LittleEndian(at, s16);
                return s16;
            case ProjectionSourceType.UInt16:
                var u16 = (ushort)index;
                BinaryPrimitives.WriteUInt16LittleEndian(at, u16);
                return u16;
            case ProjectionSourceType.Int32:
                var s32 = index >= 0
                    ? new[] { int.MinValue, -1, 0, 1, int.MaxValue }[index % 5]
                    : (int)rng.NextInt64(int.MinValue, 1L + int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(at, s32);
                return s32;
            case ProjectionSourceType.UInt32:
                var u32 = index >= 0 ? new[] { 0u, 1u, uint.MaxValue }[index % 3] : (uint)rng.NextInt64(0, 1L << 32);
                BinaryPrimitives.WriteUInt32LittleEndian(at, u32);
                return u32;
            case ProjectionSourceType.Int64:
                var s64 = rng.NextInt64(long.MinValue, long.MaxValue) >> rng.Next(0, 40);
                BinaryPrimitives.WriteInt64LittleEndian(at, s64);
                return s64;
            case ProjectionSourceType.UInt64:
                var u64 = (ulong)rng.NextInt64(long.MinValue, long.MaxValue) >> rng.Next(0, 40);
                BinaryPrimitives.WriteUInt64LittleEndian(at, u64);
                return u64;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }
    }

    private static Codec Token(string token) => token switch
    {
        "u8" => Codec.U8.Saturate(),
        "i8" => Codec.I8,
        "u16" => Codec.U16,
        "i16" => Codec.I16.Saturate(),
        "u32" => Codec.U32.Saturate(),
        "i32" => Codec.I32,
        "varu" => Codec.VarUInt.Saturate(),
        "vari" => Codec.VarInt,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null),
    };
}
