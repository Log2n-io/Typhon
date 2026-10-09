using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Typhon.Protocol.Tests;

/// <summary>
/// Golden vectors for the <c>DEBUG</c> sub-blocks the engine sends (09 § 15): <c>GRID</c> and the experimental <c>PUSH_GEOMETRY</c> in each of its three
/// shapes. The expectation carries the decoder's call log and the payload as <see cref="DebugGrid.Read"/> and <see cref="PushGeometry.Read"/> decode it, so
/// a layout change shows as a changed vector, and a decode that disagrees with the encoder's inputs fails before it can be committed.
/// </summary>
[TestFixture]
public class GoldenDebugTests
{
    private const uint Tick = 70_000;

    private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()));

    [Test]
    public void Grid()
    {
        var grid = new DebugGrid(-8192, -4096.5, 0, 250, 66, 33, 1);
        var bytes = Frame(DebugSubTypes.Grid, (ref WireWriter w) => grid.Write(ref w), out var payload);

        Assert.That(DebugGrid.Read(payload), Is.EqualTo(grid));
        Golden.Assert("debug-grid", bytes, Expectation("A DEBUG block holding one GRID: a flat 66 × 33 grid of 250 m cells from (-8192, -4096.5).", bytes,
            GridJson(DebugGrid.Read(payload))));
    }

    [Test]
    public void PushGeometrySphere()
    {
        ulong[] rows = [0b00100, 0b01110, 0b11111, 0b01110, 0b00100];
        var bytes = Frame(DebugSubTypes.PushGeometry, (ref WireWriter w) =>
        {
            PushGeometry.WriteSphere(ref w, PushGeometryFlags.ViewComplete, 1234.5, -77.25, 0, 500, 10.5, 2);
            PushGeometry.WriteWindow(ref w, 3, -2, 0, 5, rows);
        }, out var payload);

        var g = PushGeometry.Read(payload);
        Assert.Multiple(() =>
        {
            Assert.That(g.Shape, Is.EqualTo(PushShape.Sphere));
            Assert.That(g.Flags, Is.EqualTo(PushGeometryFlags.ViewComplete));
            Assert.That((g.AnchorX, g.AnchorY, g.AnchorZ), Is.EqualTo((1234.5, -77.25, 0d)));
            Assert.That((g.RadiusM, g.SlackM, g.Level), Is.EqualTo((500d, 10.5, 2)));
            Assert.That((g.WindowX, g.WindowY, g.WindowZ, g.Window), Is.EqualTo((3, -2, 0, 5)));
            Assert.That(g.Rows, Is.EqualTo(rows));
            Assert.That(g.Delivered(5, 0, 0), Is.True, "the centre cell");
            Assert.That(g.Delivered(3, -2, 0), Is.False, "a corner cell");
            Assert.That(g.Delivered(8, 0, 0), Is.False, "past the window");
        });

        Golden.Assert("debug-push-geometry-sphere", bytes, Expectation(
            "A flat sphere's PUSH_GEOMETRY with VIEW_COMPLETE: anchor, R′ 500, h 10.5, level 2, and a 5-wide window of one byte per row from cell (3, -2).",
            bytes, GeometryJson(g)));
    }

    [Test]
    public void PushGeometryDeepRegion()
    {
        double[] vertices = [0, 0, 0, 1000, 0, 0, 0, 1000, 0, 0, 0, 1000];
        var rows = Enumerable.Range(0, 81).Select(i => i % 3 == 0 ? 0x1FFUL : (ulong)i).ToArray();
        var bytes = Frame(DebugSubTypes.PushGeometry, (ref WireWriter w) =>
        {
            PushGeometry.WriteRegion(ref w, PushGeometryFlags.Deep, 3, vertices, 1500, 2000);
            PushGeometry.WriteWindow(ref w, -1, -1, -1, 9, rows);
        }, out var payload);

        var g = PushGeometry.Read(payload);
        Assert.Multiple(() =>
        {
            Assert.That(g.Shape, Is.EqualTo(PushShape.Region));
            Assert.That(g.Flags, Is.EqualTo(PushGeometryFlags.Deep));
            Assert.That(g.Dims, Is.EqualTo(3));
            Assert.That(g.Vertices, Is.EqualTo(vertices));
            Assert.That((g.Held, g.NearBudget), Is.EqualTo((1500, 2000)));
            Assert.That((g.WindowX, g.WindowY, g.WindowZ, g.Window), Is.EqualTo((-1, -1, -1, 9)));
            Assert.That(g.Rows, Is.EqualTo(rows), "81 rows of two bytes each: a 9-wide deep window");
            Assert.That(g.Delivered(7, -1, -1), Is.True, "row 0 is full");
            Assert.That(g.Delivered(7, 0, -1), Is.False, "row 1 holds 1: only its first cell");
        });

        Golden.Assert("debug-push-geometry-region", bytes, Expectation(
            "A deep region's PUSH_GEOMETRY: a four-vertex tetrahedron, held 1500 of a 2000 near budget, and a 9-wide window of 81 two-byte rows.", bytes,
            GeometryJson(g)));
    }

    [Test]
    public void PushGeometryWorldAndAFlatRegionWithNoHull()
    {
        var bytes = Frame(DebugSubTypes.PushGeometry, (ref WireWriter w) => PushGeometry.WriteWorld(ref w, PushGeometryFlags.None, 0x0000_0001_0000_0020UL),
            out var world);
        var flat = new byte[64];
        var fw = new WireWriter(flat);
        PushGeometry.WriteRegion(ref fw, PushGeometryFlags.None, 2, [], 0, 0);
        PushGeometry.WriteWindow(ref fw, 0, 0, 0, 0, []);

        var g = PushGeometry.Read(world);
        var none = PushGeometry.Read(fw.Written);
        Assert.Multiple(() =>
        {
            Assert.That((g.Shape, g.Cursor, g.Window), Is.EqualTo((PushShape.World, 0x0000_0001_0000_0020UL, 0)));
            Assert.That((none.Shape, none.Dims, none.Vertices.Length, none.Window, none.Rows.Length), Is.EqualTo((PushShape.Region, 2, 0, 0, 0)));
            Assert.That(none.Delivered(0, 0, 0), Is.False);
        });

        Golden.Assert("debug-push-geometry-world", bytes, Expectation("A World session's PUSH_GEOMETRY: its cursor, as a little-endian u64.", bytes,
            GeometryJson(g)));
    }

    [Test]
    public void AMalformedPushGeometryIsRejected()
    {
        var buffer = new byte[64];
        var w = new WireWriter(buffer);
        PushGeometry.WriteWorld(ref w, PushGeometryFlags.None, 7);
        var world = w.Written.ToArray();

        Assert.Multiple(() =>
        {
            Assert.Throws<WireFormatException>(() => PushGeometry.Read([9, 0]), "an unknown shape");
            Assert.Throws<WireFormatException>(() => PushGeometry.Read(world.Append((byte)0).ToArray()), "a byte after the payload");
            Assert.Throws<WireFormatException>(() => PushGeometry.Read(world[..^1]), "a truncated cursor");
            Assert.Throws<WireFormatException>(() => PushGeometry.Read([(byte)PushShape.Region, 0, 4, 0]), "a region of four dimensions");
            Assert.Throws<WireFormatException>(() => PushGeometry.Read([(byte)PushShape.Sphere, 0, .. new byte[32], 0, 0, 0, 0, 65]),
                "a window wider than 64 cells");
        });
    }

    /// <summary>
    /// A DEBUG block holding two sub-blocks, the first of a sub-type no decoder knows: it must be skipped by its declared
    /// length and the second must arrive intact.
    /// </summary>
    /// <remarks>
    /// 03 § 12 W26 names this as a required golden, and neither suite had it: every other vector here writes exactly one
    /// sub-block, so the skip-by-length path — the entire reason the block is framed the way it is, and the property a
    /// forward-compatible client relies on — was never exercised end to end on either side.
    /// </remarks>
    [Test]
    public void UnknownSubBlockIsSkipped()
    {
        var grid = new DebugGrid(512, -1024, 0, 64, 12, 9, 1);
        var scratch = new byte[512];
        var unknown = new WireWriter(scratch);
        unknown.WriteU32(0xDEADBEEF);
        unknown.WriteU8(7);
        var unknownPayload = unknown.Written.ToArray();

        var gridScratch = new byte[512];
        var gridWriter = new WireWriter(gridScratch);
        grid.Write(ref gridWriter);
        var gridPayload = gridWriter.Written.ToArray();

        var buffer = new byte[8192];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.None);
        TickWriter.WriteDebug(ref w, [(UnknownSubType, unknownPayload), (DebugSubTypes.Grid, gridPayload)]);
        var bytes = w.Written.ToArray();

        Assert.That(DebugGrid.Read(gridPayload), Is.EqualTo(grid), "the known sub-block still decodes on its own");
        Golden.Assert("debug-unknown-then-grid", bytes, Expectation(
            "A DEBUG block of two sub-blocks: an unknown sub-type 0x42 that a decoder must skip by its length, then a GRID it must still read.",
            bytes, GridJson(DebugGrid.Read(gridPayload))));
    }

    /// <summary>A sub-type no decoder recognises, and none may: chosen outside every assigned value.</summary>
    private const byte UnknownSubType = 0x42;

    private delegate void WritePayload(ref WireWriter w);

    // A TICK holding one DEBUG block of one sub-block; the payload comes back as the decoder saw it.
    private static byte[] Frame(byte subType, WritePayload write, out byte[] payload)
    {
        var scratch = new byte[4096];
        var p = new WireWriter(scratch);
        write(ref p);
        payload = p.Written.ToArray();

        var buffer = new byte[8192];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, Tick, TickFlags.None);
        TickWriter.WriteDebug(ref w, [(subType, payload)]);
        return w.Written.ToArray();
    }

    private static JsonObject Expectation(string description, byte[] bytes, JsonObject decoded)
    {
        // Push geometry is in realm metres: decoded by a session holding a realm (12-realms § 5.2).
        var sink = new RecordingSink();
        var frame = CatalogSamples.KitchenFrame;
        TickReader.Read(bytes, Plan, ref frame, ref sink);
        return new JsonObject
        {
            ["description"] = description,
            ["catalog"] = "catalog-kitchen-sink",

            // The frame the session held: none of these messages carries a REALM, and a decoder without one refuses a PUSH_GEOMETRY outright (12-realms § 5.2),
            // so another language's decoder cannot reproduce the read without being told which frame to start from.
            ["frame"] = CatalogSamples.FrameJson(CatalogSamples.KitchenFrame),
            ["log"] = sink.Log.DeepClone(),
            ["decoded"] = decoded,
        };
    }

    private static JsonObject GridJson(DebugGrid g) => new()
    {
        ["originX"] = Golden.Bits(g.OriginX), ["originY"] = Golden.Bits(g.OriginY), ["originZ"] = Golden.Bits(g.OriginZ), ["cellM"] = Golden.Bits(g.CellM),
        ["dimX"] = g.DimX, ["dimY"] = g.DimY, ["dimZ"] = g.DimZ,
    };

    private static JsonObject GeometryJson(PushGeometry g)
    {
        var json = new JsonObject { ["shape"] = g.Shape.ToString(), ["flags"] = (int)g.Flags };
        switch (g.Shape)
        {
            case PushShape.World:
                json["cursor"] = g.Cursor.ToString("x16");
                return json;
            case PushShape.Sphere:
                json["anchor"] = Golden.Bits([g.AnchorX, g.AnchorY, g.AnchorZ]);
                json["radiusM"] = Golden.Bits(g.RadiusM);
                json["slackM"] = Golden.Bits(g.SlackM);
                json["level"] = g.Level;
                break;
            default:
                json["dims"] = g.Dims;
                json["vertices"] = Golden.Bits(g.Vertices);
                json["held"] = g.Held;
                json["nearBudget"] = g.NearBudget;
                break;
        }

        json["window"] = new JsonArray(g.WindowX, g.WindowY, g.WindowZ, g.Window);
        json["rows"] = new JsonArray(g.Rows.Select(r => (JsonNode)r.ToString("x")).ToArray());
        return json;
    }
}
