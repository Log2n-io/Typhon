using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Golden vectors for every codec at its boundaries (05-sdks § 4, decisions W1–W10): varint edges, quantization min/mid/max, half-float subnormals and
/// ties, angle wrap, smallest-three edge cases, <c>tickLo</c> wrap. Each vector's <c>.bin</c> is the concatenated encodings; its <c>.json</c> lists every
/// case's input, byte range and decoded bit pattern.
/// </summary>
/// <remarks>
/// A client asserts two things per case: decoding the byte range yields the decoded bits exactly, and — for every codec a command can carry — encoding the
/// input yields the byte range exactly. The second is what makes a TypeScript-encoded command decodable by the server without a live connection.
/// </remarks>
[TestFixture]
public class GoldenCodecTests
{
    private const uint FrameTick = 70_000;

    private static readonly double Nan = double.NaN;
    private static readonly double Inf = double.PositiveInfinity;

    [Test]
    public void Integers()
    {
        Vector("codec-u8", "u8 at 0, 1, 255.", new CatalogCodec { Kind = CodecKind.U8 }, S(0), S(1), S(255));
        Vector("codec-i8", "i8 at its extremes and zero.", new CatalogCodec { Kind = CodecKind.I8 }, S(-128), S(0), S(127));
        Vector("codec-u16", "u16 at 0 and 65535, little-endian.", new CatalogCodec { Kind = CodecKind.U16 }, S(0), S(0x1234), S(65535));
        Vector("codec-i16", "i16 at its extremes.", new CatalogCodec { Kind = CodecKind.I16 }, S(-32768), S(-1), S(32767));
        Vector("codec-u32", "u32 including a value ≥ 2³¹, which a JavaScript decoder must keep non-negative.",
            new CatalogCodec { Kind = CodecKind.U32 }, S(0), S(2147483648), S(uint.MaxValue));
        Vector("codec-i32", "i32 at its extremes.", new CatalogCodec { Kind = CodecKind.I32 }, S(int.MinValue), S(-1), S(int.MaxValue));
        Vector("codec-varu", "varu at every length edge: 127/128, 16383/16384, 2²¹, 2²⁸, and the 32-bit maximum.", new CatalogCodec { Kind = CodecKind.Varu },
            S(0), S(127), S(128), S(16383), S(16384), S(2097151), S(2097152), S(268435455), S(268435456), S(uint.MaxValue));
        Vector("codec-varu-lenient", "Decode-only: an over-long varu is accepted as long as it fits 32 bits in five bytes (03 § 2).",
            new CatalogCodec { Kind = CodecKind.Varu }, Raw(0x80, 0x00), Raw(0x80, 0x80, 0x80, 0x80, 0x00), Raw(0xFF, 0x80, 0x00));
        Vector("codec-vari", "vari zigzag at small magnitudes and both extremes.", new CatalogCodec { Kind = CodecKind.Vari },
            S(0), S(-1), S(1), S(-64), S(64), S(int.MinValue), S(int.MaxValue));
        Vector("codec-entityRef", "entityRef is a varu netId; 0 is null.", new CatalogCodec { Kind = CodecKind.EntityRef }, S(0), S(1), S(2097152));
        Vector("codec-tickLo", $"tickLo against frame tick {FrameTick}: past ticks within 2¹⁶, across the 16-bit wrap.",
            new CatalogCodec { Kind = CodecKind.TickLo }, S(FrameTick), S(FrameTick - 1), S(65535), S(65536), S(FrameTick - 65535));
        Vector("codec-tickLo-early", "tickLo against frame tick 100, before the first wrap: the rebuilt tick of a low code above the frame's wraps below 0.",
            new CatalogCodec { Kind = CodecKind.TickLo }, 100, S(100), S(0), Raw(0xFF, 0xFF));
        Vector("codec-tickLo-max", "tickLo against the largest frame tick, 2³² − 1.", new CatalogCodec { Kind = CodecKind.TickLo }, uint.MaxValue,
            S(uint.MaxValue), S(uint.MaxValue - 65535));
    }

    [Test]
    public void Floats()
    {
        Vector("codec-f32", "f32: narrowing ties to even, signed zero, infinities, NaN encoded as 0x7FC00000 and decoded as the canonical NaN.",
            new CatalogCodec { Kind = CodecKind.F32 },
            S(0), S(-0.0), S(1.5), S(0.1), S(3.4028234663852886e38), S(Inf), S(-Inf), S(Nan), Raw(0x00, 0x00, 0xC0, 0xFF), Raw(0x01, 0x00, 0x80, 0x7F));
        Vector("codec-f16-decode", "Decode-only f16: a negative quiet NaN (0xFE00) and a signalling NaN (0x7C01) both decode as the canonical NaN.",
            new CatalogCodec { Kind = CodecKind.F16 }, Raw(0x00, 0xFE), Raw(0x01, 0x7C));
        Vector("codec-f16", "f16 (W10): 65504, overflow to ∞ at 65520, subnormals and their carry, the tie 1 + 2⁻¹¹ to even, double rounding, NaN as 0x7E00.",
            new CatalogCodec { Kind = CodecKind.F16 },
            S(0), S(-0.0), S(1), S(65504), S(65519.99), S(65520), S(Math.ScaleB(1, -24)), S(Math.ScaleB(1, -25)), S(Math.ScaleB(3, -25)),
            S(Math.ScaleB(1, -14) - Math.ScaleB(1, -25)), S(1 + Math.ScaleB(1, -11)), S(1.00048828125000022204), S(Inf), S(-Inf), S(Nan));
    }

    [Test]
    public void Quantized()
    {
        var quant = new CatalogCodec { Kind = CodecKind.Quant, Min = [-40], Max = [88], Bits = 8 };
        Vector("codec-quant", "quant over [−40, 88) at 8 bits (W2): min, below min, max − step, the tie at max − step/2, max, NaN, ±∞, a tie at k + 0.5.",
            quant, S(-40), S(-41), S(87.5), S(87.75), S(88), S(89), S(Nan), S(Inf), S(-Inf), S(0.25), S(-39.75));
        Vector("codec-quant-16", "quant at 16 bits: min, the top code at max, a tie.",
            new CatalogCodec { Kind = CodecKind.Quant, Min = [-1], Max = [1], Bits = 16 }, S(-1), S(1), S(1 - 1.0 / 65536), S(1.0 / 65536));
        Vector("codec-quant-24", "quant at 24 bits, whose top byte ≥ 0x80 exercises the u24 path, up to the top code.",
            new CatalogCodec { Kind = CodecKind.Quant, Min = [0], Max = [1], Bits = 24 }, S(0), S(0.5), S(0.999999), S(1));
        Vector("codec-quant-32", "quant at 32 bits, codes ≥ 2³¹, up to the top code.",
            new CatalogCodec { Kind = CodecKind.Quant, Min = [-1000], Max = [1000], Bits = 32 }, S(0), S(999.99), S(-1000), S(1000));
        Vector("codec-pos2", "pos2 over ±8192 at 24 bits (W3): corners, origin (code 8 388 608), max clamp, one NaN axis.", CatalogSamples.Pos2(),
            V(-8192, -8192), V(0, 0), V(8191.9990234375, -0.0009765625), V(8192, 8192), V(Nan, 12.5));
        Vector("codec-pos3", "pos3 at 16 bits, 6 bytes per value.",
            new CatalogCodec { Kind = CodecKind.Pos3, Min = [-1024, -64, -1024], Max = [1024, 64, 1024], Bits = 16 }, V(0, 0, 0), V(-1024, 63.99, 1023.97));
        Vector("codec-pos3-24", "pos3 at 24 bits, 9 bytes per value.",
            new CatalogCodec { Kind = CodecKind.Pos3, Min = [-8192, -8192, -8192], Max = [8192, 8192, 8192], Bits = 24 }, V(1.5, -2.25, 8191.999));
        Vector("codec-vec2", "vec2 with scale 0.5 at 8 bits (W4): symmetric clamp, ties away from zero; the wire-only code −128 decodes as −127.",
            new CatalogCodec { Kind = CodecKind.Vec2, Scale = 0.5, Bits = 8 }, V(0, 0), V(0.25, -0.25), V(1000, -1000), V(Nan, 63.5), Raw(0x80, 0x80));
        Vector("codec-vec3", "vec3 with scale 0.001 at 16 bits.",
            new CatalogCodec { Kind = CodecKind.Vec3, Scale = 0.001, Bits = 16 }, V(1, -1, 0.0005), V(32.767, -32.768, 0));
        Vector("codec-vec2-24", "vec2 with scale 0.25 at 24 bits: sign extension of an i24, the symmetric clamp ±(2²³ − 1), a tie, and the wire-only code "
            + "−2²³ decoding as −(2²³ − 1).",
            new CatalogCodec { Kind = CodecKind.Vec2, Scale = 0.25, Bits = 24 }, V(0, -0.25), V(1e9, -1e9), V(0.125, -0.125),
            Raw(0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF));
        Vector("codec-vec3-32", "vec3 with scale 0.001 at 32 bits: the clamp ±(2³¹ − 1), and the wire-only code −2³¹ decoding as −(2³¹ − 1).",
            new CatalogCodec { Kind = CodecKind.Vec3, Scale = 0.001, Bits = 32 }, V(1, -1, 0.0005), V(1e12, -1e12, 0),
            Raw(0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x00, 0x00, 0x00));
        Vector("codec-unorm", "unorm at 8 bits (W6): 0, 1, 0.5 → 128, 1/255, over-range and NaN.",
            new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 }, S(0), S(1), S(0.5), S(1.0 / 255), S(1.5), S(-0.5), S(Nan));
        Vector("codec-unorm-16", "unorm at 16 bits.", new CatalogCodec { Kind = CodecKind.Unorm, Bits = 16 }, S(0.25), S(1));
        Vector("codec-unorm-24", "unorm at 24 bits: 0, 1, the tie at 0.5, one step.", new CatalogCodec { Kind = CodecKind.Unorm, Bits = 24 },
            S(0), S(1), S(0.5), S(1.0 / 16777215));
        Vector("codec-unorm-32", "unorm at 32 bits, codes ≥ 2³¹: 1, 0.5, one step.", new CatalogCodec { Kind = CodecKind.Unorm, Bits = 32 },
            S(1), S(0.5), S(1.0 / 4294967295));
        Vector("codec-snorm", "snorm at 8 bits (W6): ±1 exact, ±0.5/127 ties, clamp, NaN; the wire-only code −128 decodes as −1.",
            new CatalogCodec { Kind = CodecKind.Snorm, Bits = 8 }, S(-1), S(0), S(1), S(0.5 / 127), S(-0.5 / 127), S(1.5), S(-1.5), S(Nan), Raw(0x80));
        Vector("codec-snorm-24", "snorm at 24 bits: sign extension of an i24, ±1, a tie, and the wire-only code −2²³ decoding as −1.",
            new CatalogCodec { Kind = CodecKind.Snorm, Bits = 24 }, S(-1), S(1), S(-0.5 / 8388607), Raw(0x00, 0x00, 0x80), Raw(0xFF, 0xFF, 0xFF));
        Vector("codec-snorm-32", "snorm at 32 bits: ±1, a quarter, and the wire-only code −2³¹ decoding as −1.",
            new CatalogCodec { Kind = CodecKind.Snorm, Bits = 32 }, S(-1), S(1), S(0.25), Raw(0x00, 0x00, 0x00, 0x80));
        Vector("codec-angle", "angle at 16 bits (W7): 0, −0, π wraps to −π, one step below π, 2π to 0, ties ±(n + ½), beyond 2⁵³ to 0, NaN.",
            new CatalogCodec { Kind = CodecKind.Angle, Bits = 16 },
            S(0), S(-0.0), S(Math.PI), S(-Math.PI), S(Math.PI - WireMath.Tau / 65536), S(WireMath.Tau), S(3 * WireMath.Tau + 1),
            S(2.5 * WireMath.Tau / 65536), S(-2.5 * WireMath.Tau / 65536), S(1e300), S(Nan));
        Vector("codec-angle-8", "angle at 8 bits.", new CatalogCodec { Kind = CodecKind.Angle, Bits = 8 }, S(1), S(-3));
        Vector("codec-angle-24", "angle at 24 bits, and at the 2⁵³ guard: a code of exactly 2⁵³ wraps, the next representable is refused to 0.",
            new CatalogCodec { Kind = CodecKind.Angle, Bits = 24 },
            S(1), S(-Math.PI), S(Math.ScaleB(1, 53) * WireMath.Tau / Math.ScaleB(1, 24)), S(Math.ScaleB(1, 54) * WireMath.Tau / Math.ScaleB(1, 24)));
        Vector("codec-angle-32", "angle at 32 bits.", new CatalogCodec { Kind = CodecKind.Angle, Bits = 32 }, S(1), S(-3), S(Math.PI));
        var s = Math.Sqrt(0.5);
        Vector("codec-quat3", "quat3 (W8): identity, each dropped index, the tie, a negative largest component, components at ±√½, NaN, unnormalized; "
            + "the wire-only component code −512 decodes as −511.",
            new CatalogCodec { Kind = CodecKind.Quat3 },
            V(0, 0, 0, 1), V(1, 0, 0, 0), V(0, 1, 0, 0), V(0, 0, 1, 0), V(0.5, -0.5, 0.5, -0.5), V(0.1, 0.2, 0.3, -0.9), V(s, 0, 0, s), V(-s, 0, 0, s),
            V(Nan, 0, 0, 1), V(0, 0, 3, 4), Raw(0x03, 0x08, 0x00, 0x00));
    }

    /// <summary>The velocity codec needs the position step it is expressed in; the vector records it so a client can build the same plan.</summary>
    [Test]
    public void Velocity()
    {
        var catalog = CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink());
        var drone = CatalogPlan.Compile(catalog).ArchetypeByName("Drone").Position;
        var step = drone.Vel.VelocityPositionStep;

        var cases = new List<JsonObject>();
        var buffer = new byte[256];
        var w = new WireWriter(buffer);
        var tie = step[0] * 0.5 / 4;
        foreach (var v in new[]
        {
            V(0, 0, 0), V(step[0], -step[1] / 4, step[2] * 40), V(1e9, -1e9, Nan), V(tie, -tie, 127 * step[2] / 4), V(-127 * step[0] / 4, 0, 0),
        })
        {
            cases.Add(EncodeCase(ref w, drone.Vel, FrameTick, FieldValue.Of(v.Numbers)));
        }

        cases.Add(EncodeCase(ref w, drone.Vel, FrameTick, Raw(0x80, 0x7F, 0x01)));
        Finish("codec-vel3", "vel3 inside the kitchen-sink Drone position (W5): quantaDiv 4, 8 bits — clamps, ±½-unit ties, exactly ±L, and the wire-only "
            + "code −128 decoding as −127.", drone.Vel, cases, w.Written.ToArray(), FrameTick, new JsonObject { ["positionStep"] = Golden.Bits(step) });

        var creature = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.Swg())).ArchetypeByName("Creature").Position;
        var swgStep = creature.Vel.VelocityPositionStep;
        cases = [];
        w = new WireWriter(buffer);
        foreach (var v in new[] { V(swgStep[0], -swgStep[1]), V(-2, 2), V(swgStep[0] * 0.5 / 16, -swgStep[1] * 0.5 / 16) })
        {
            cases.Add(EncodeCase(ref w, creature.Vel, FrameTick, v));
        }

        cases.Add(EncodeCase(ref w, creature.Vel, FrameTick, Raw(0x00, 0x80, 0xFF, 0xFF)));
        Finish("codec-vel2", "vel2 as catalog-swg declares it: 16 bits, quantaDiv 16 — sign extension at 16 bits, the clamp, a tie, and −32768.", creature.Vel,
            cases, w.Written.ToArray(), FrameTick, new JsonObject { ["positionStep"] = Golden.Bits(swgStep) });

        var wide = CatalogPlan.Compile(CatalogSerializer.Canonicalize(new Catalog
        {
            Protocol = new CatalogProtocolVersion { Major = 2 }, App = new CatalogApp { Name = "Vel24" }, Tick = new CatalogTick { PeriodUs = 1, PingHz = 1 },
            Limits = new CatalogLimits { FrameBytes = 1 << 20, ClientMessageBytes = 1024 },
            Archetypes =
            [
                new CatalogArchetype
                {
                    Name = "M", Groups = [], Fields = [],
                    Position = new CatalogPosition
                    {
                        Kind = CatalogPosition.MotionKind, Model = CatalogPosition.LinearModel, Pos = CatalogSamples.Pos2(),
                        Vel = new CatalogCodec { Kind = CodecKind.Vel2, QuantaDiv = 16, Bits = 24 },
                    },
                },
            ],
        })).ArchetypeByName("M").Position;
        var wideStep = wide.Vel.VelocityPositionStep;
        cases = [];
        w = new WireWriter(buffer);
        foreach (var v in new[] { V(wideStep[0], -wideStep[1]), V(1e9, -1e9), V(wideStep[0] * 0.5 / 16, -wideStep[1] * 0.5 / 16) })
        {
            cases.Add(EncodeCase(ref w, wide.Vel, FrameTick, v));
        }

        cases.Add(EncodeCase(ref w, wide.Vel, FrameTick, Raw(0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF)));
        Finish("codec-vel2-24", "vel2 at 24 bits, quantaDiv 16 (W5): sign extension of an i24 (0xFFFFFF is −1), the clamp ±(2²³ − 1), a tie, and the "
            + "wire-only code −2²³ decoding as −(2²³ − 1).", wide.Vel, cases, w.Written.ToArray(), FrameTick,
            new JsonObject { ["positionStep"] = Golden.Bits(wideStep) });
    }

    [Test]
    public void TextAndBytes()
    {
        var buffer = new byte[512];
        var w = new WireWriter(buffer);
        var str = new FieldPlanBuilder(new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 16 }).Plan;
        var cases = new List<JsonObject>();
        foreach (var text in new[] { "", "hi", "Tatooine ☀", new string('x', 16) })
        {
            var start = w.Position;
            w.WriteStr(text, 16);
            cases.Add(new JsonObject
            {
                ["text"] = Golden.Hex(Encoding.UTF8.GetBytes(text)), ["offset"] = start, ["length"] = w.Position - start,
            });
        }

        Finish("codec-str", "str with maxBytes 16: empty, ASCII, multi-byte UTF-8, exactly at the cap.", str, cases, w.Written.ToArray());

        w = new WireWriter(buffer);
        cases = [];
        foreach (var text in new[] { "\uFEFFhi", "hi\uFEFF" })
        {
            var start = w.Position;
            w.WriteStr(text, 16);
            cases.Add(new JsonObject
            {
                ["text"] = Golden.Hex(Encoding.UTF8.GetBytes(text)), ["offset"] = start, ["length"] = w.Position - start,
            });
        }

        Finish("codec-str-bom", "str keeps a U+FEFF byte-order mark (EF BB BF) wherever it stands: a decoder must not strip a leading one.", str, cases,
            w.Written.ToArray());

        w = new WireWriter(buffer);
        var blob = new FieldPlanBuilder(new CatalogCodec { Kind = CodecKind.Blob, MaxBytes = 200 }).Plan;
        cases = [];
        foreach (var bytes in new[] { Array.Empty<byte>(), [0xDE, 0xAD], new byte[128] })
        {
            var start = w.Position;
            w.WriteBlob(bytes, 200);
            cases.Add(new JsonObject { ["bytes"] = Golden.Hex(bytes), ["offset"] = start, ["length"] = w.Position - start });
        }

        Finish("codec-blob", "blob with maxBytes 200: empty, short, and a 128-byte blob whose length takes two varint bytes.", blob, cases,
            w.Written.ToArray());

        w = new WireWriter(buffer);
        var fixedBytes = new FieldPlanBuilder(new CatalogCodec { Kind = CodecKind.Bytes, N = 4 }).Plan;
        w.WriteBytes([1, 2, 3, 4]);
        Finish("codec-bytes", "bytes with n 4: raw, no length.", fixedBytes,
            [new JsonObject { ["bytes"] = "01020304", ["offset"] = 0, ["length"] = 4 }], w.Written.ToArray());
    }

    [Test]
    public void Lists()
    {
        var region = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink())).CommandByName(BuiltInCommands.ClientRegion);
        var vertices = Array.Find(region.Body.Fields, f => f.Name == BuiltInCommands.RegionVerticesField);
        var buffer = new byte[512];
        var w = new WireWriter(buffer);
        var cases = new List<JsonObject>();
        foreach (var count in new[] { 3, 4, 16 })
        {
            var flat = new double[count * 2];
            for (var i = 0; i < count; i++)
            {
                flat[2 * i] = -8192 + i * 1000.5;
                flat[2 * i + 1] = 8191 - i * 333.25;
            }

            var start = w.Position;
            FieldCodec.WriteSection(ref w, new SectionPlanBuilder(vertices).Plan, _ => new FieldValue { Numbers = flat });
            var r = new WireReader(buffer.AsSpan(start, w.Position - start));
            var sink = new RecordingSink();
            FieldCodec.ReadSection(ref r, new SectionPlanBuilder(vertices).Plan, FrameTick, ref sink);
            var decoded = (JsonObject)sink.Log[0]!;
            cases.Add(new JsonObject
            {
                ["input"] = Golden.Bits(flat), ["offset"] = start, ["length"] = w.Position - start, ["count"] = count,
                ["decoded"] = decoded["values"]!.DeepClone(),
            });
        }

        Finish("codec-list", "list<pos2, 3..16> — ClientRegion's vertices (W28): the minimum, a quad, and the maximum.", vertices, cases, w.Written.ToArray());
    }

    /// <summary>
    /// W12: packed fields share the section's leading pack, least significant bit first — a value straddling a byte boundary, a 24-bit value at offset 7
    /// that spans four bytes, and a value past bit 31 in a fifth byte whose padding is zero.
    /// </summary>
    [Test]
    public void Packs()
    {
        var fields = new[]
        {
            new CatalogField { Name = "a", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 3 } },
            new CatalogField { Name = "b", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 4 } },
            new CatalogField { Name = "c", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 24 } },
            new CatalogField { Name = "d", Codec = new CatalogCodec { Kind = CodecKind.Bool } },
            new CatalogField { Name = "e", Codec = new CatalogCodec { Kind = CodecKind.U8 } },
            new CatalogField { Name = "f", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 2 } },
        };

        var plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(new Catalog
        {
            Protocol = new CatalogProtocolVersion { Major = 2 }, App = new CatalogApp { Name = "Packs" }, Tick = new CatalogTick { PeriodUs = 1, PingHz = 1 },
            Limits = new CatalogLimits { FrameBytes = 1 << 20, ClientMessageBytes = 1024 },
            Events = [new CatalogEvent { Name = "P", Scope = "all", Fields = fields }],
        })).EventByName("P");

        var sets = new[]
        {
            new double[] { 5, 9, 0xABCDEF, 1, 7, 2 },
            new double[] { 7, 15, 0xFFFFFF, 0, 255, 3 },
            new double[] { 0, 0, 0, 1, 0, 0 },
        };

        var buffer = new byte[64];
        var w = new WireWriter(buffer);
        var cases = new List<JsonObject>();
        foreach (var set in sets)
        {
            var start = w.Position;
            FieldCodec.WriteSection(ref w, plan.Body, f => FieldValue.Of(set[Array.FindIndex(fields, x => x.Name == f.Name)]));
            var values = new JsonObject();
            foreach (var f in plan.Body.Fields)
            {
                values[f.Name] = Golden.Bits(set[Array.FindIndex(fields, x => x.Name == f.Name)]);
            }

            cases.Add(new JsonObject { ["values"] = values, ["offset"] = start, ["length"] = w.Position - start });
        }

        var fieldJson = new JsonArray();
        foreach (var f in plan.Body.Fields)
        {
            fieldJson.Add(new JsonObject { ["name"] = f.Name, ["codec"] = Golden.CodecJson(f.Codec), ["bitOffset"] = f.Packed ? f.BitOffset : -1 });
        }

        Golden.Assert("section-packs", w.Written.ToArray(), new JsonObject
        {
            ["description"] = "A section with packed fields 3 + 4 + 24 + 1 + 2 bits — the 24-bit field at offset 7 spans four bytes; a 5-byte pack whose six "
                + "padding bits are zero — followed by a byte-aligned u8 (W12).",
            ["fields"] = fieldJson,
            ["packBytes"] = plan.Body.PackBytes,
            ["cases"] = new JsonArray([.. cases]),
        });
    }

    private static void Vector(string name, string description, CatalogCodec codec, params FieldValue[] inputs) =>
        Vector(name, description, codec, FrameTick, inputs);

    private static void Vector(string name, string description, CatalogCodec codec, uint frameTick, params FieldValue[] inputs)
    {
        var plan = new FieldPlanBuilder(codec).Plan;
        var buffer = new byte[1024];
        var w = new WireWriter(buffer);
        var cases = new List<JsonObject>();
        foreach (var input in inputs)
        {
            cases.Add(EncodeCase(ref w, plan, frameTick, input));
        }

        Finish(name, description, plan, cases, w.Written.ToArray(), frameTick);
    }

    /// <summary>
    /// Encodes one case and decodes it back. A value carrying <see cref="FieldValue.Bytes"/> instead of numbers is a decode-only case: its raw bytes are
    /// written verbatim, and its expectation has no <c>input</c> — a wire form no encoder produces but every decoder must read.
    /// </summary>
    private static JsonObject EncodeCase(ref WireWriter w, FieldPlan plan, uint frameTick, FieldValue input)
    {
        var start = w.Position;
        if (input.Numbers != null)
        {
            FieldCodec.WriteNumber(ref w, plan, input.Numbers);
        }
        else
        {
            w.WriteBytes(input.Bytes);
        }

        var length = w.Position - start;
        Span<double> decoded = stackalloc double[4];
        var r = new WireReader(w.Written.Slice(start, length));
        FieldCodec.ReadNumber(ref r, plan, frameTick, decoded);
        Assert.That(r.IsAtEnd, Is.True, $"{plan.Codec.Type}: decode consumed {r.Position} of {length} bytes");

        var json = new JsonObject();
        if (input.Numbers != null)
        {
            json["input"] = Golden.Bits(input.Numbers);
        }

        json["offset"] = start;
        json["length"] = length;
        json["decoded"] = Golden.Bits(decoded[..plan.Components]);
        return json;
    }

    private static void Finish(string name, string description, FieldPlan plan, List<JsonObject> cases, byte[] bytes, uint frameTick = FrameTick,
        JsonObject extra = null)
    {
        var json = new JsonObject
        {
            ["description"] = description,
            ["codec"] = Golden.CodecJson(plan.Codec),
            ["frameTick"] = frameTick,
        };

        foreach (var (key, value) in extra ?? new JsonObject())
        {
            json[key] = value!.DeepClone();
        }

        json["cases"] = new JsonArray([.. cases]);
        Golden.Assert(name, bytes, json);
    }

    private static FieldValue S(double v) => FieldValue.Of(v);

    private static FieldValue Raw(params byte[] bytes) => new() { Bytes = bytes };

    private static FieldValue V(params double[] v) => FieldValue.Of(v);

    /// <summary>Compiles a lone codec into a plan through a one-field event, the same way a catalog would.</summary>
    private sealed class FieldPlanBuilder
    {
        public FieldPlanBuilder(CatalogCodec codec)
        {
            var catalog = new Catalog
            {
                Protocol = new CatalogProtocolVersion { Major = 2 },
                App = new CatalogApp { Name = "Codec" },
                Tick = new CatalogTick { PeriodUs = 1, PingHz = 1 },
                Limits = new CatalogLimits { FrameBytes = 1 << 20, ClientMessageBytes = 1024 },
                Events = [new CatalogEvent { Name = "E", Scope = "all", Fields = [new CatalogField { Name = "v", Codec = codec }] }],
            };

            Plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(catalog)).EventByName("E").Body.Fields[0];
        }

        public FieldPlan Plan { get; }
    }

    private sealed class SectionPlanBuilder
    {
        public SectionPlanBuilder(FieldPlan field)
        {
            var catalog = new Catalog
            {
                Protocol = new CatalogProtocolVersion { Major = 2 },
                App = new CatalogApp { Name = "Section" },
                Tick = new CatalogTick { PeriodUs = 1, PingHz = 1 },
                Limits = new CatalogLimits { FrameBytes = 1 << 20, ClientMessageBytes = 1024 },
                Events = [new CatalogEvent { Name = "E", Scope = "all", Fields = [field.Field] }],
            };

            Plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(catalog)).EventByName("E").Body;
        }

        public SectionPlan Plan { get; }
    }
}
