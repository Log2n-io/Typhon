using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>The exact wire's archetype field types (13 § 2.1): 64-bit integers, a double, fixed shapes, a char.</summary>
[Component("Typhon.Test.Proj.Exact", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjExact
{
    [Field]
    public long Id;

    [Field]
    public ulong Balance;

    [Field]
    public long Delta;

    [Field]
    public double Rate;

    [Field]
    public Point3F Spot;

    [Field]
    public AABB3D Box;

    [Field]
    public QuaternionF Spin;

    [Field]
    public char Initial;
}

/// <summary>The owner section's exact fields: a 64-bit varint and a point of doubles.</summary>
[Component("Typhon.Test.Proj.Vault", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjVault
{
    [Field]
    public ulong Secret;

    [Field]
    public Point2D Home;
}

[Archetype]
partial class ProjExactThing : Archetype<ProjExactThing>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjExact> Exact = Register<ProjExact>();
    public static readonly Comp<ProjVault> Vault = Register<ProjVault>();
}

/// <summary>An event carrying the exact wire's types: they default to their exact codecs (13 § 2.1).</summary>
public struct ProjAudit
{
    public long Amount;
    public ulong Total;
    public double At;
    public Point3F Corner;
    public int Seq;
}

/// <summary>
/// The pairing table on the exact wire (13 § 2.3, E-2): 64-bit integers and doubles travel whole, a fixed shape as a count of its element, and the losses
/// for no gain stay refused.
/// </summary>
[TestFixture]
public class ExactPairingTests
{
    private static ColumnPath Classify(Type source, Codec codec, bool message = false) =>
        CodecPairing.Classify(source, codec.Catalog, codec.Saturating, "Field 'f'", message);

    private static Codec Resolve(Type source, Codec codec, out string shape) => CodecPairing.Resolve(source, codec, "Field 'f'", out shape);

    [Test]
    public void A64BitIntegerInA64BitCodecIsExact()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(long), Codec.I64), Is.EqualTo(ColumnPath.ExactInteger));
            Assert.That(Classify(typeof(long), Codec.VarInt64), Is.EqualTo(ColumnPath.ExactInteger));
            Assert.That(Classify(typeof(ulong), Codec.U64), Is.EqualTo(ColumnPath.ExactInteger));
            Assert.That(Classify(typeof(ulong), Codec.VarUInt64), Is.EqualTo(ColumnPath.ExactInteger));
            Assert.That(Classify(typeof(int), Codec.I64), Is.EqualTo(ColumnPath.ExactInteger), "a wider codec covers a narrower source");
            Assert.That(Classify(typeof(uint), Codec.VarUInt64), Is.EqualTo(ColumnPath.ExactInteger));
        });
    }

    [Test]
    public void A64BitSignChangeNeedsSaturate()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(ulong), Codec.I64), "u64 above 2⁶³ does not fit an i64");
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(long), Codec.U64), "a negative long does not fit a u64");
            Assert.That(Classify(typeof(ulong), Codec.I64.Saturate()), Is.EqualTo(ColumnPath.NarrowingInteger));
            Assert.That(Classify(typeof(long), Codec.VarUInt64.Saturate()), Is.EqualTo(ColumnPath.NarrowingInteger));
        });
    }

    [Test]
    public void ADoubleInF64CopiesItsBitsAndAFloatOrAnIntegerInF64IsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(double), Codec.F64), Is.EqualTo(ColumnPath.ExactDouble));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Classify(typeof(float), Codec.F64)).Message, Does.Contain("Codec.F32"),
                "four wasted bytes: the refusal names the exact codec");
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(long), Codec.F64), "an integer in a float codec gains nothing");
            Assert.That(Assert.Throws<InvalidOperationException>(() => Classify(typeof(double), Codec.I64.Saturate())).Message, Does.Contain("Codec.F64"));
        });
    }

    [TestCase(typeof(long), "i64", null)]
    [TestCase(typeof(ulong), "u64", null)]
    [TestCase(typeof(double), "f64", null)]
    [TestCase(typeof(float), "f32", null)]
    [TestCase(typeof(int), "i32", null)]
    [TestCase(typeof(ushort), "u16", null)]
    [TestCase(typeof(char), "u16", "char")]
    public void ExactResolvesToTheTypesExactCodec(Type source, string token, string shape)
    {
        var resolved = Resolve(source, Codec.Exact, out var hint);
        Assert.Multiple(() =>
        {
            Assert.That(resolved.Token, Is.EqualTo(token));
            Assert.That(resolved.Catalog.Count, Is.Zero, "one value: no count");
            Assert.That(hint, Is.EqualTo(shape));
            Assert.That(Classify(source, resolved), Is.Not.EqualTo(ColumnPath.None));
        });
    }

    [TestCase(typeof(Point2F), "f32", 2, "point2")]
    [TestCase(typeof(Point3F), "f32", 3, "point3")]
    [TestCase(typeof(Point4D), "f64", 4, "point4")]
    [TestCase(typeof(QuaternionF), "f32", 4, "quat")]
    [TestCase(typeof(QuaternionD), "f64", 4, "quat")]
    [TestCase(typeof(AABB2F), "f32", 4, "aabb2")]
    [TestCase(typeof(AABB3D), "f64", 6, "aabb3")]
    [TestCase(typeof(BSphere2F), "f32", 3, "bsphere2")]
    [TestCase(typeof(BSphere3D), "f64", 4, "bsphere3")]
    public void AShapeIsACountOfItsElement(Type source, string token, int count, string shape)
    {
        var resolved = Resolve(source, Codec.Exact, out var hint);
        Assert.Multiple(() =>
        {
            Assert.That((resolved.Token, resolved.Catalog.Count, hint), Is.EqualTo((token, count, shape)));
            Assert.That(Classify(source, resolved), Is.EqualTo(token == "f64" ? ColumnPath.ExactDouble : ColumnPath.ExactSingle));
            Assert.That(FieldShape.Of(source).Count, Is.EqualTo(count));
        });
    }

    [Test]
    public void AShapeTakesADeclaredLossyCodecPerComponentAndRefusesTheWrongCount()
    {
        Assert.Multiple(() =>
        {
            var f16 = Resolve(typeof(Point3F), Codec.F16, out _);
            Assert.That((f16.Token, f16.Catalog.Count), Is.EqualTo(("f16", 3)), "the count comes from the shape");
            Assert.That(Classify(typeof(Point3F), f16), Is.EqualTo(ColumnPath.Quantizing));
            var quant = Resolve(typeof(BSphere3F), Codec.Quant(-1000, 1000, 16).Count(4), out _);
            Assert.That(quant.Catalog.Count, Is.EqualTo(4), "a stated count that agrees stands");
            Assert.That(Classify(typeof(AABB3D), Resolve(typeof(AABB3D), Codec.F32, out _)), Is.EqualTo(ColumnPath.Quantizing),
                "a double shape in f32 is a declared narrowing");

            Assert.That(Assert.Throws<InvalidOperationException>(() => Resolve(typeof(Point3F), Codec.F32.Count(4), out _)).Message,
                Does.Contain("3 components"));
            Assert.Throws<InvalidOperationException>(() => Classify(typeof(Point3F), Resolve(typeof(Point3F), Codec.F64, out _)),
                "f64 on a float shape: four wasted bytes per component");
            Assert.Throws<InvalidOperationException>(() => Resolve(typeof(long), Codec.I64.Count(2), out _), "a count belongs on a shape");
            Assert.Throws<InvalidOperationException>(() => Resolve(typeof(Point3F), Codec.Str(8), out _), "text is not a component codec");
        });
    }

    [Test]
    public void AVectorOrQuaternionCodecFitsOnlyItsShape()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Classify(typeof(Point3F), Resolve(typeof(Point3F), Codec.Vec3(0.01, 16), out _)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(Point2D), Resolve(typeof(Point2D), Codec.Vec2(0.01, 16), out _)), Is.EqualTo(ColumnPath.Quantizing));
            Assert.That(Classify(typeof(QuaternionF), Resolve(typeof(QuaternionF), Codec.Quat3, out _)), Is.EqualTo(ColumnPath.Quaternion));
            Assert.Throws<InvalidOperationException>(() => Resolve(typeof(Point2F), Codec.Vec3(0.01, 16), out _));
            Assert.Throws<InvalidOperationException>(() => Resolve(typeof(AABB2F), Codec.Vec2(0.01, 16), out _), "a box is not a vector");
            Assert.Throws<InvalidOperationException>(() => Resolve(typeof(Point4F), Codec.Quat3, out _), "a point4 is not a quaternion");
        });
    }

    [Test]
    public void MessageFieldsDefaultToTheirExactCodecs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageContract.DefaultCodec(typeof(long), out _).Token, Is.EqualTo("i64"));
            Assert.That(MessageContract.DefaultCodec(typeof(ulong), out _).Token, Is.EqualTo("u64"));
            Assert.That(MessageContract.DefaultCodec(typeof(double), out _).Token, Is.EqualTo("f64"));
            var point = MessageContract.DefaultCodec(typeof(Point3F), out _);
            Assert.That((point.Token, point.Catalog.Count), Is.EqualTo(("f32", 3)));
        });
    }

    [Test]
    public void TheCodecDeclarationRefusesACountItCannotCarry()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Codec.F32.Count(3).ToString(), Is.EqualTo("f32x3"));
            Assert.Throws<InvalidOperationException>(() => Codec.Bool.Count(2));
            Assert.Throws<InvalidOperationException>(() => Codec.Str(8).Count(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Codec.F32.Count(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Codec.F32.Count(17));
            Assert.Throws<InvalidOperationException>(() => Codec.Exact.Count(2));
            Assert.That(Codec.Exact.IsDeclared, Is.True);
            Assert.That(Codec.Exact.ToString(), Is.EqualTo("exact"));
        });
    }

    /// <summary>
    /// The protocol's raw-byte and list codecs have no engine source (13 § 2.1): no field is a byte array, and a collection travels as <c>coll</c>. An
    /// attribute naming one is refused, saying so — before this, a command field declared <c>blob</c> decoded and was dropped without a word.
    /// </summary>
    [Test]
    public void ACodecNoFieldTypeCarriesIsRefusedByTheAttributePath()
    {
        Assert.Multiple(() =>
        {
            foreach (var kind in new[] { CodecKind.Bytes, CodecKind.Blob, CodecKind.List })
            {
                var refusal = Assert.Throws<NotSupportedException>(() => Codec.Declared<int>(kind, maxBytes: 16), kind.ToString());
                Assert.That(refusal.Message, Does.Contain("no field type").And.Contain("Codec.Coll"), kind.ToString());
            }
        });
    }

    /// <summary>
    /// A Fraction divides two scalars: over a shape it would compile to one ratio per component while the catalog declares one, so it is refused where it
    /// is written rather than desyncing the stream.
    /// </summary>
    [Test]
    public void AFractionOverAShapeIsRefused()
    {
        var refused = Assert.Throws<ArgumentException>(() => new SubscriptionsRegistry().Archetype<ProjExactThing>(a => a
            .Fraction(ProjExactThing.Exact, x => x.Spot, x => x.Spot, 8, name: "ratio")));
        Assert.Multiple(() =>
        {
            Assert.That(refused.Message, Does.Contain("'ratio'"), "the refusal names the field");
            Assert.That(refused.Message, Does.Contain(nameof(Point3F)), "and the shape it divides");
        });
    }
}

/// <summary>
/// Integer ranges in 64 bits (13 § 2.3): a signed low bound and an unsigned high bound, since every range contains 0 — and the 64-bit clamps built on
/// them, at each edge where a signed and an unsigned value meet.
/// </summary>
[TestFixture]
public class IntegerRangeTests
{
    [Test]
    public void ARangeComparesSignedLowAndUnsignedHighBounds()
    {
        var u64 = new IntegerRange(0, ulong.MaxValue);
        var i64 = new IntegerRange(long.MinValue, long.MaxValue);
        var u32 = new IntegerRange(0, uint.MaxValue);
        Assert.Multiple(() =>
        {
            Assert.That(u64.Covers(u32) && i64.Covers(u32), Is.True);
            Assert.That(u64.Covers(i64), Is.False, "a u64 holds no negative");
            Assert.That(i64.Covers(u64), Is.False, "an i64 holds no value above 2⁶³ − 1");
            Assert.That(i64.Contains(-1L) && !u64.Contains(-1L), Is.True);
            Assert.That(u64.Contains(ulong.MaxValue) && !i64.Contains(ulong.MaxValue), Is.True);
            Assert.That((u64.SignedMax, u32.SignedMax), Is.EqualTo((long.MaxValue, (long)uint.MaxValue)));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new IntegerRange(1, 2), "a range that excludes 0 has no 64-bit spelling here");
        });
    }

    [Test]
    public void ACommandStoresA64BitWireValueClampedToItsField()
    {
        static ulong Store(CommandFieldElement element, int size, ulong bits, bool signedWire)
        {
            Span<byte> payload = stackalloc byte[8];
            new CommandFieldBinding("v", 0, 1, element, size).StoreInteger64(payload, [bits], signedWire);
            return BinaryPrimitives.ReadUInt64LittleEndian(payload);
        }

        Assert.Multiple(() =>
        {
            Assert.That((int)Store(CommandFieldElement.I32, 4, unchecked((ulong)long.MinValue), true), Is.EqualTo(int.MinValue));
            Assert.That((int)Store(CommandFieldElement.I32, 4, ulong.MaxValue, false), Is.EqualTo(int.MaxValue), "u64's top into an int");
            Assert.That((uint)Store(CommandFieldElement.U32, 4, unchecked((ulong)-5L), true), Is.Zero, "a negative into a uint");
            Assert.That(Store(CommandFieldElement.U64, 8, unchecked((ulong)-1L), true), Is.Zero, "−1 into a ulong");
            Assert.That(Store(CommandFieldElement.I64, 8, ulong.MaxValue, false), Is.EqualTo((ulong)long.MaxValue), "2⁶⁴ − 1 into a long");
            Assert.That((sbyte)Store(CommandFieldElement.I8, 1, unchecked((ulong)-100L), true), Is.EqualTo((sbyte)-100), "in range, exact");
        });
    }

    [Test]
    public void ASaturatingEventFieldClampsA64BitSourceIntoItsCodec()
    {
        static ulong Load(CommandFieldElement element, int size, ulong stored, CodecKind codec)
        {
            Span<byte> payload = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, stored);
            var binding = new EventFieldBinding(null, 0, 1, element, size, entity: false)
            {
                Saturating = true,
                Clamp = CodecPairing.CodeRange(new CatalogCodec { Kind = codec }),
            };
            Span<ulong> into = stackalloc ulong[1];
            binding.LoadInteger64(payload, into);
            return into[0];
        }

        Assert.Multiple(() =>
        {
            Assert.That(Load(CommandFieldElement.U64, 8, ulong.MaxValue, CodecKind.I64), Is.EqualTo((ulong)long.MaxValue), "a ulong into i64 clamps");
            Assert.That(Load(CommandFieldElement.I64, 8, unchecked((ulong)-7L), CodecKind.U64), Is.Zero, "a negative long into u64 clamps to 0");
            Assert.That(Load(CommandFieldElement.I64, 8, unchecked((ulong)long.MinValue), CodecKind.Vari64), Is.EqualTo(unchecked((ulong)long.MinValue)));
            Assert.That(Load(CommandFieldElement.U32, 4, uint.MaxValue, CodecKind.Varu64), Is.EqualTo((ulong)uint.MaxValue));
        });
    }
}

/// <summary>The exact wire's column walks (13 § 4): the bits of a double, and a <see cref="ulong"/> narrowed into a signed 64-bit codec, counted.</summary>
[TestFixture]
public class ExactColumnWalkTests
{
    private static CompiledField Field(ProjectionSourceType source, CatalogCodec codec, ColumnPath path, int stride)
    {
        var (min, max) = CodecPairing.ClampRange(codec);
        return new CompiledField
        {
            Name = "f", ComponentSize = stride, FieldOffsetInComponent = 0, RatioOffsetInComponent = -1, SourceType = source, Codec = codec,
            CodecKind = codec.Kind, Path = path, IntMin = min, IntMax = max,
        };
    }

    [Test]
    public void ADoubleColumnIsItsBitsWithEveryNaNCanonical()
    {
        double[] values = [0d, -0d, 0.1, double.Epsilon, double.MaxValue, double.NegativeInfinity, BitConverter.Int64BitsToDouble(0x7FF0000000000001)];
        var column = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(column.AsSpan(i * 8), values[i]);
        }

        var codes = new ulong[values.Length];
        var field = Field(ProjectionSourceType.Double, new CatalogCodec { Kind = CodecKind.F64 }, ColumnPath.ExactDouble, 8);
        ProjectionColumnWalk.Quantize(field, new ProjectionColumn(column, 8, 0), (1UL << values.Length) - 1, codes);
        Assert.Multiple(() =>
        {
            for (var i = 0; i < values.Length - 1; i++)
            {
                Assert.That(codes[i], Is.EqualTo((ulong)BitConverter.DoubleToInt64Bits(values[i])), $"value {i}");
            }

            Assert.That(codes[^1], Is.EqualTo(0x7FF8000000000000UL), "a signalling NaN goes out as the canonical NaN");
        });
    }

    [Test]
    public void A64BitIntegerColumnIsItsBits()
    {
        ulong[] values = [0, 1, (1UL << 53) + 1, ulong.MaxValue, 1UL << 63];
        var column = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(column.AsSpan(i * 8), values[i]);
        }

        var codes = new ulong[values.Length];
        var u64 = Field(ProjectionSourceType.UInt64, new CatalogCodec { Kind = CodecKind.U64 }, ColumnPath.ExactInteger, 8);
        ProjectionColumnWalk.Quantize(u64, new ProjectionColumn(column, 8, 0), (1UL << values.Length) - 1, codes);
        Assert.That(codes, Is.EqualTo(values), "u64 from a ulong: the value");

        var i64 = Field(ProjectionSourceType.Int64, new CatalogCodec { Kind = CodecKind.I64 }, ColumnPath.ExactInteger, 8);
        ProjectionColumnWalk.Quantize(i64, new ProjectionColumn(column, 8, 0), (1UL << values.Length) - 1, codes);
        Assert.That(codes, Is.EqualTo(values), "i64 from a long: its two's complement, the same bits");
    }

    [Test]
    public void AULongNarrowedIntoASigned64BitCodecClampsAndCountsEveryClamp()
    {
        ulong[] values = [0, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue];
        var column = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(column.AsSpan(i * 8), values[i]);
        }

        var codes = new ulong[values.Length];
        var field = Field(ProjectionSourceType.UInt64, new CatalogCodec { Kind = CodecKind.Vari64 }, ColumnPath.NarrowingInteger, 8);
        var clamps = ProjectionColumnWalk.Quantize(field, new ProjectionColumn(column, 8, 0), (1UL << values.Length) - 1, codes);
        Assert.Multiple(() =>
        {
            Assert.That(clamps, Is.EqualTo(2), "2⁶³ and 2⁶⁴ − 1 clamp; long.MaxValue itself does not");
            Assert.That(codes, Is.EqualTo(new[] { 0UL, (ulong)long.MaxValue, (ulong)long.MaxValue, (ulong)long.MaxValue }));
        });
    }
}

/// <summary>
/// The exact wire end to end (13 § 3, E-1, E-5, E-11): an archetype's 64-bit, double, shape and char fields, and an event's, reach <c>Typhon.Client</c>'s
/// replica exactly — values a double could not carry — and the catalog names each count and shape once.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class ExactWireTests : TestBase<ExactWireTests>
{
    private const string Profile = "world";

    private static DatabaseEngine Engine(IServiceProvider services)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjExact>();
        dbe.RegisterComponentFromAccessor<ProjVault>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new System.Numerics.Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new System.Numerics.Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjExactThing>(a => a
            .Motion(ProjExactThing.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .OnEnter(ProjExactThing.Exact, x => x.Id, Codec.Exact, name: "id")
            .Field(ProjExactThing.Exact, x => x.Balance, Codec.Exact, name: "balance", group: "money")
            .Field(ProjExactThing.Exact, x => x.Delta, Codec.VarInt64, name: "delta", group: "money")
            .Field(ProjExactThing.Exact, x => x.Rate, Codec.Exact, name: "rate", group: "money")
            .Field(ProjExactThing.Exact, x => x.Spot, Codec.Exact, name: "spot", group: "shape")
            .Field(ProjExactThing.Exact, x => x.Box, Codec.F32, name: "box", group: "shape")
            .Field(ProjExactThing.Exact, x => x.Spin, Codec.Quat3, name: "spin", group: "shape")
            .Field(ProjExactThing.Exact, x => x.Initial, Codec.Exact, name: "initial", group: "shape")
            .Owner(o => o
                .Field(ProjExactThing.Vault, x => x.Secret, Codec.VarUInt64, name: "secret")
                .Field(ProjExactThing.Vault, x => x.Home, Codec.Exact, name: "home")));
        subs.Profile(Profile, p => p.World().Of<ProjExactThing>());
        subs.Event<ProjAudit>(e => e.Broadcast());
    }

    private static ProjExact Values(long id, ulong balance, long delta, double rate) => new()
    {
        Id = id, Balance = balance, Delta = delta, Rate = rate,
        Spot = new Point3F { X = 1.5f, Y = -2.25f, Z = 0.1f },
        Box = new AABB3D { MinX = -1e300, MinY = -0.5, MinZ = 0.1, MaxX = 1e300, MaxY = 0.5, MaxZ = 2.75 },
        Spin = new QuaternionF { X = 0, Y = 0.6f, Z = 0, W = 0.8f },
        Initial = 'Ж',
    };

    private static ProjVault Vault(ulong balance, double rate) => new()
    {
        Secret = balance ^ 0xFFFF_0000_0000_0000UL,
        Home = new Point2D { X = rate, Y = -rate },
    };

    private static ProjBounds At(float x) => new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = -0.5f, MaxX = x + 0.5f, MaxY = 0.5f }, Speed = 1f };

    [Test]
    public void ExactFieldsReachTheReplicaExactlyAndTheCatalogNamesEachShapeOnce()
    {
        var dbe = Engine(ServiceProvider);
        EntityId[] ids = new EntityId[2];
        using (var tx = dbe.CreateQuickTransaction())
        {
            var first = Values(long.MinValue, (1UL << 53) + 1, -(1L << 53) - 1, double.Epsilon);
            var second = Values(-1, ulong.MaxValue, long.MaxValue, 0.1);
            var firstVault = Vault((1UL << 53) + 1, double.Epsilon);
            var secondVault = Vault(ulong.MaxValue, 0.1);
            ids[0] = tx.Spawn<ProjExactThing>(ProjExactThing.Bounds.Set(At(10f)), ProjExactThing.Exact.Set(in first), ProjExactThing.Vault.Set(in firstVault));
            ids[1] = tx.Spawn<ProjExactThing>(ProjExactThing.Bounds.Set(At(20f)), ProjExactThing.Exact.Set(in second),
                ProjExactThing.Vault.Set(in secondVault));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, Declare, nameof(ExactWireTests), replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, Profile)[0];
        Assert.That(harness.Sessions.SetControlled(session, ids[1]), Is.True);
        harness.RunTick(1);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));

        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(ProjExactThing));
        var replica = harness.Replica(session);
        var netIds = replica.NetIds(archetype.Idx);
        Assert.That(netIds, Has.Length.EqualTo(2));
        // Spawn order is netId order for a fresh world, but the test reads each by its own id rather than assume it.
        var byId = netIds.ToDictionary(n => unchecked((long)replica.Integers(archetype.Idx, n, "id")[0]));

        Assert.Multiple(() =>
        {
            var a = byId[long.MinValue];
            var b = byId[-1];
            Assert.That(replica.Integers(archetype.Idx, a, "balance")[0], Is.EqualTo((1UL << 53) + 1), "2⁵³ + 1: a double would round it");
            Assert.That(replica.Integers(archetype.Idx, b, "balance")[0], Is.EqualTo(ulong.MaxValue));
            Assert.That(unchecked((long)replica.Integers(archetype.Idx, a, "delta")[0]), Is.EqualTo(-(1L << 53) - 1));
            Assert.That(unchecked((long)replica.Integers(archetype.Idx, b, "delta")[0]), Is.EqualTo(long.MaxValue));
            Assert.That(replica.Numbers(archetype.Idx, a, "rate")[0], Is.EqualTo(double.Epsilon), "the smallest subnormal, bit for bit");
            Assert.That(replica.Numbers(archetype.Idx, a, "spot"), Is.EqualTo(new double[] { 1.5f, -2.25f, 0.1f }), "f32 × 3, exact");
            Assert.That(replica.Numbers(archetype.Idx, a, "box"),
                Is.EqualTo(new double[] { double.NegativeInfinity, -0.5, (float)0.1, double.PositiveInfinity, 0.5, 2.75 }),
                "a double box in f32: each component narrowed as f32 narrows, ±1e300 to ±∞");
            Assert.That(replica.Numbers(archetype.Idx, a, "spin"), Is.EqualTo(new double[] { 0, 0.6, 0, 0.8 }).Within(1e-3), "quat3, within its quantum");
            Assert.That(replica.Numbers(archetype.Idx, a, "initial")[0], Is.EqualTo((double)'Ж'), "a char as its UTF-16 code unit");
            Assert.That(replica.Store.Anomalies, Is.Zero);

            // The owner section (SELF): a varu64 and an f64 × 2, only to the session controlling the entity.
            var self = replica.Store.Self;
            var secret = Array.Find(self.Archetype.OwnerFields, f => f.Name == "secret");
            var home = Array.Find(self.Archetype.OwnerFields, f => f.Name == "home");
            Assert.That(self.Integers[secret.Ordinal][0], Is.EqualTo(ulong.MaxValue ^ 0xFFFF_0000_0000_0000UL), "an owner varu64, exact");
            Assert.That(self.Numbers[home.Ordinal], Is.EqualTo(new[] { 0.1, -0.1 }), "an owner point2 of doubles, exact");

            var catalog = harness.CatalogPlan.Catalog.Archetypes[archetype.Idx];
            var spot = Array.Find(catalog.Fields, f => f.Name == "spot");
            var box = Array.Find(catalog.Fields, f => f.Name == "box");
            var initial = Array.Find(catalog.Fields, f => f.Name == "initial");
            Assert.That(catalog.Fields, Has.Length.EqualTo(8), "a count field is one catalog field, however many code rows it compiled to");
            Assert.That((spot.Codec.Type, spot.Codec.Count, spot.Shape), Is.EqualTo(("f32", 3, "point3")));
            Assert.That((box.Codec.Type, box.Codec.Count, box.Shape), Is.EqualTo(("f32", 6, "aabb3")));
            Assert.That((initial.Codec.Type, initial.Shape), Is.EqualTo(("u16", "char")));
        });

        // A state record: the money group changes and carries the new 64-bit values.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjExactThing>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
                var exact = cluster.GetSpan(ProjExactThing.Exact);
#pragma warning restore TYPHON009
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    exact[slot].Balance = 0x8000_0000_0000_0001UL;
                    harness.Subscriptions.Commands.Replicate(in cluster, slot);
                }
            }

            accessor.Dispose();
            tx.Commit();
        }

        harness.Subscriptions.Commands.Emit(new ProjAudit
        {
            Amount = long.MinValue + 1, Total = ulong.MaxValue - 1, At = -0d, Corner = new Point3F { X = 1, Y = float.PositiveInfinity, Z = -2.5f }, Seq = 3,
        });
        harness.RunTick(2);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));

        var events = replica.Events;
        Assert.Multiple(() =>
        {
            foreach (var netId in netIds)
            {
                Assert.That(replica.Integers(archetype.Idx, netId, "balance")[0], Is.EqualTo(0x8000_0000_0000_0001UL), "the state record carried it");
            }

            Assert.That(events.Received, Has.Count.EqualTo(1));
            var amount = events.Integers.Single(i => i.Field == "Amount");
            var total = events.Integers.Single(i => i.Field == "Total");
            Assert.That(unchecked((long)amount.Bits), Is.EqualTo(long.MinValue + 1), "an i64 event field, exact");
            Assert.That(total.Bits, Is.EqualTo(ulong.MaxValue - 1), "a u64 event field, exact");
            Assert.That(BitConverter.DoubleToInt64Bits(events.Received[0].Fields["At"]), Is.EqualTo(BitConverter.DoubleToInt64Bits(-0d)), "f64 keeps −0");
            Assert.That(events.Received[0].Fields["Corner"], Is.EqualTo(1d), "a point's first component (the recorder keeps the first)");
            Assert.That(harness.Subscriptions.Events.Rejected, Is.Zero);
        });

        var audit = harness.CatalogPlan.EventByName(nameof(ProjAudit));
        var corner = Array.Find(audit.Body.Fields, f => f.Name == "Corner");
        Assert.That((corner.Codec.Type, corner.Codec.Count, corner.Field.Shape), Is.EqualTo(("f32", 3, "point3")), "an event's point defaults to f32 × 3");
    }
}
