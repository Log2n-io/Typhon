using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// #957 — the properties the catalog's skip-on-match rests on: a digest that survives declaration order, moves when the contract moves, and never leaks the
/// server's memory layout; plus the wire order and reserved indices canonicalization assigns (W11, W27).
/// </summary>
/// <remarks>
/// A client stores the hash it was sent and offers it back to skip ≈ 5 KB on its next connection; the server decides whether it matches. Every failure mode
/// here is therefore silent by construction — a hash that varies between processes makes the skip never fire, and one that fails to move when a field is
/// renamed makes a client decode the new wire with the old plan.
/// </remarks>
[TestFixture]
public class CatalogCanonicalizationTests
{
    [Test]
    public void TheSameDeclarationAlwaysProducesTheSameBytesAndTheSameHash()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg()), Is.EqualTo(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg())));
            Assert.That(CatalogSerializer.ComputeHash(CatalogSamples.Swg()), Is.EqualTo(CatalogSerializer.ComputeHash(CatalogSamples.Swg())));
        });
    }

    /// <summary>
    /// Declaration order is an authoring detail, not a contract. It must reach neither the digest nor the bytes — and because <c>idx</c> and field order are
    /// wire facts, the only way to have both is to derive them from the sort.
    /// </summary>
    [Test]
    public void ReorderingDeclarationsChangesNeitherTheHashNorTheBytes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CatalogSerializer.ComputeHash(CatalogSamples.SwgReordered()), Is.EqualTo(CatalogSerializer.ComputeHash(CatalogSamples.Swg())));
            Assert.That(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.SwgReordered()), Is.EqualTo(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg())));
        });
    }

    [Test]
    public void RenamingAFieldChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());
        var renamed = CatalogSamples.Swg();
        var fields = renamed.Archetypes[1].Fields;
        var i = Array.FindIndex(fields, f => f.Name == "hp");
        fields[i] = new CatalogField { Name = "health", Codec = fields[i].Codec, Group = fields[i].Group, Smoothing = fields[i].Smoothing };

        Assert.That(CatalogSerializer.ComputeHash(renamed), Is.Not.EqualTo(baseline));
    }

    [Test]
    public void AddingAFieldChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());
        var extended = CatalogSamples.Swg();
        var creature = extended.Archetypes[1];
        extended.Archetypes[1] = new CatalogArchetype
        {
            Name = creature.Name,
            Groups = creature.Groups,
            Position = creature.Position,
            Fields =
            [
                .. creature.Fields,
                new CatalogField { Name = "stamina", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 }, Group = "vitals" },
            ],
        };

        Assert.That(CatalogSerializer.ComputeHash(extended), Is.Not.EqualTo(baseline));
    }

    /// <summary>One more bit on the same field must move the digest: a client decoding it the old way would read the wrong number of bytes.</summary>
    [Test]
    public void ChangingACodecParameterChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());
        var widened = CatalogSamples.Swg();
        var fields = widened.Archetypes[1].Fields;
        var i = Array.FindIndex(fields, f => f.Name == "hp");
        fields[i] = new CatalogField
        {
            Name = "hp", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 16 }, Group = fields[i].Group, Smoothing = fields[i].Smoothing,
        };

        Assert.That(CatalogSerializer.ComputeHash(widened), Is.Not.EqualTo(baseline));
    }

    /// <summary>Moving a field from one group to another re-lays both sections' bytes, so it must move the digest even though nothing was renamed.</summary>
    [Test]
    public void MovingAFieldBetweenGroupsChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());
        var moved = CatalogSamples.Swg();
        var fields = moved.Archetypes[1].Fields;
        var i = Array.FindIndex(fields, f => f.Name == "hp");
        fields[i] = new CatalogField { Name = "hp", Codec = fields[i].Codec, Group = "state", Smoothing = fields[i].Smoothing };

        Assert.That(CatalogSerializer.ComputeHash(moved), Is.Not.EqualTo(baseline));
    }

    /// <summary>D2 and D3, asserted by scanning the emitted bytes: no CLR type name and no storage offset may appear anywhere in a catalog.</summary>
    [Test]
    public void TheEmittedCatalogNamesNoClrTypeAndNoStorageOffset()
    {
        var json = Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.KitchenSink()));

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("Typhon."), "a CLR namespace in the catalog would make renaming a C# type a wire break");
            Assert.That(json, Does.Not.Contain("System."));
            foreach (var forbidden in new[] { "offset", "Offset", "clrType", "fullName", "componentOffset" })
            {
                Assert.That(json, Does.Not.Contain(forbidden), $"'{forbidden}' describes server memory, which a client has no business knowing");
            }
        });
    }

    [Test]
    public void CanonicalizeDoesNotMutateItsInput()
    {
        var declared = CatalogSamples.Swg();
        var firstArchetypeBefore = declared.Archetypes[0].Name;
        var fieldOrderBefore = declared.Archetypes[1].Fields.Select(f => f.Name).ToArray();

        CatalogSerializer.Canonicalize(declared);

        Assert.Multiple(() =>
        {
            Assert.That(declared.Archetypes[0].Name, Is.EqualTo(firstArchetypeBefore));
            Assert.That(declared.Archetypes[1].Fields.Select(f => f.Name), Is.EqualTo(fieldOrderBefore));
        });
    }

    [Test]
    public void ArchetypeIndicesAreDenseAndFollowTheOrdinalSort()
    {
        var canonical = CatalogSerializer.Canonicalize(CatalogSamples.SwgReordered());

        Assert.Multiple(() =>
        {
            Assert.That(canonical.Archetypes.Select(a => a.Idx), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(canonical.Archetypes.Select(a => a.Name), Is.EqualTo(new[] { "Creature", "Player" }));
        });
    }

    /// <summary>
    /// W11: the field array is wire order — the onEnter section, then each group in canonical order, packed fields first within a section, then by name.
    /// A group that sorts earlier places its fields first even when their names sort later.
    /// </summary>
    [Test]
    public void FieldsAreInWireOrderSectionThenPackedThenName()
    {
        var drone = CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()).Archetypes.Single(a => a.Name == "Drone");

        Assert.That(drone.Fields.Select(f => f.Name), Is.EqualTo(new[]
        {
            "label", "serial",                                               // onEnter: byte-aligned only, by name
            "armed", "lights", "stance",                                     // flags: all packed
            "heading", "rotation", "thrust",                                 // motion2
            "battery", "lastHit", "target", "temperature", "tilt",           // vitals
        }));
    }

    [Test]
    public void PackedFieldsComeFirstWithinACommandBody()
    {
        var steer = CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()).Commands.Single(c => c.Name == "Steer");

        Assert.That(steer.Fields.Select(f => f.Name), Is.EqualTo(new[] { "boost", "stance", "heading", "note", "speed" }));
    }

    /// <summary>W27: built-ins keep a reserved index and applications start at the base, so enabling a built-in never renumbers an application entry.</summary>
    [Test]
    public void BuiltInsKeepReservedIndicesAndApplicationEntriesStartAtTheBase()
    {
        var canonical = CatalogSerializer.Canonicalize(CatalogSamples.Swg());
        var withoutRegion = CatalogSamples.Swg();
        var noRegion = new Catalog
        {
            Protocol = withoutRegion.Protocol, App = withoutRegion.App, Tick = withoutRegion.Tick, Limits = withoutRegion.Limits,
            Archetypes = withoutRegion.Archetypes, Enums = withoutRegion.Enums, Events = withoutRegion.Events, Grids = withoutRegion.Grids,
            Metrics = withoutRegion.Metrics, Commands = [withoutRegion.Commands[0]],
        };

        Assert.Multiple(() =>
        {
            Assert.That(canonical.Commands.Select(c => (c.Idx, c.Name)), Is.EqualTo(new[] { (0, "ClientRegion"), (16, "MoveTo") }));
            Assert.That(CatalogSerializer.Canonicalize(noRegion).Commands.Single().Idx, Is.EqualTo(16));
            Assert.That(canonical.Events.Single().Idx, Is.EqualTo(16));
            Assert.That(canonical.Metrics.Single().Idx, Is.EqualTo(1));
        });
    }

    [Test]
    public void ApplicationMetricsStartAt32AndDefaultsAreOmitted()
    {
        var metrics = CatalogSerializer.Canonicalize(CatalogSamples.KitchenSink()).Metrics;

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Select(m => (m.Idx, m.Name)),
                Is.EqualTo(new[]
                {
                    (0, "typhon.tick.p50"), (2, "typhon.system.mean"), (9, "typhon.session.skippedFrames"), (32, "app.load"), (33, "app.queue"),
                }));
            Assert.That(metrics[0].Scope, Is.Null, "server scope is the default and is not written");
            Assert.That(metrics[2].Kind, Is.EqualTo(CatalogMetric.CounterKind));
        });
    }

    [Test]
    public void CodecsSerializeAsTheirWireTokens()
    {
        var json = Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.KitchenSink()));

        Assert.Multiple(() =>
        {
            foreach (var token in new[] { "pos2", "pos3", "vel3", "unorm", "snorm", "varu", "vari", "tickLo", "entityRef", "quat3", "list", "future" })
            {
                Assert.That(json, Does.Contain($"\"t\":\"{token}\""), token);
            }
        });
    }

    /// <summary>What a client does at WELCOME: parse the canonical bytes; the parse must re-serialize and re-hash to exactly what the server sent.</summary>
    [Test]
    public void ACanonicalCatalogRoundTripsThroughItsBytes()
    {
        var bytes = CatalogSerializer.ToCanonicalUtf8(CatalogSamples.KitchenSink());
        var parsed = CatalogSerializer.FromUtf8(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(CatalogSerializer.ToCanonicalUtf8(parsed), Is.EqualTo(bytes));
            Assert.That(CatalogSerializer.HashBytes(bytes), Is.EqualTo(CatalogSerializer.ComputeHash(CatalogSamples.KitchenSink())),
                "the digest is FNV-1a over the canonical bytes (03 § 4)");
            Assert.That(parsed.Archetypes.Single(a => a.Name == "Ledger").Fields.Single(f => f.Name == "future").Codec.Kind, Is.EqualTo(CodecKind.Unknown));
        });
    }

    /// <summary>
    /// A client refuses a catalog that is valid but not canonical: a decode plan built from swapped indices or unsorted groups would map mask bits and
    /// indices onto the wrong fields, silently.
    /// </summary>
    [TestCase("\"groups\":[\"state\",\"vitals\"]", "\"groups\":[\"vitals\",\"state\"]")]
    [TestCase("\"idx\":16,\"name\":\"Attack\"", "\"idx\":17,\"name\":\"Attack\"")]
    [TestCase("\"idx\":0,\"name\":\"Creature\"", "\"idx\":1,\"name\":\"Creature\"")]
    public void AClientRefusesANonCanonicalCatalog(string canonical, string tampered)
    {
        var json = Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg()));
        Assert.That(json, Does.Contain(canonical), "the tampering must hit something");

        Assert.Throws<CatalogException>(() => CatalogSerializer.FromUtf8(Encoding.UTF8.GetBytes(json.Replace(canonical, tampered))));
    }

    /// <summary>Hostile JSON — null elements, absurd indices — is a <see cref="CatalogException"/>, never a crash.</summary>
    [TestCase("{\"archetypes\":[null]}")]
    [TestCase("{\"events\":[{\"idx\":2147483647,\"name\":\"E\",\"scope\":\"all\",\"fields\":[]}]}")]
    [TestCase("{\"commands\":[null],\"metrics\":[null],\"grids\":[null]}")]
    [TestCase("not json")]
    public void HostileCatalogJsonIsRefusedCleanly(string json) =>
        Assert.Throws<CatalogException>(() => CatalogSerializer.FromUtf8(Encoding.UTF8.GetBytes(json)));

    /// <summary>A grid lists archetypes by declared index; canonicalization re-sorts the archetypes and must carry the grid's references with them.</summary>
    [Test]
    public void GridArchetypeIndicesFollowTheArchetypesTheyName()
    {
        var declared = CatalogSamples.Swg();
        var withPlayerOnly = new Catalog
        {
            Protocol = declared.Protocol, App = declared.App, Tick = declared.Tick, Limits = declared.Limits, Archetypes = declared.Archetypes,
            Enums = declared.Enums, Events = declared.Events, Commands = declared.Commands, Metrics = declared.Metrics,
            Grids = [new CatalogGrid { Origin = [-8192, -8192], Cell = 256, Dims = [64, 64], Archetypes = [0] }],
        };

        var canonical = CatalogSerializer.Canonicalize(withPlayerOnly);

        Assert.That(canonical.Archetypes[canonical.Grids[0].Archetypes[0]].Name, Is.EqualTo("Player"), "declared index 0 is Player, canonical index 1");
    }

    /// <summary>The digest covers everything that reaches the wire — here, a limit a client acts on (W30) and an owner section (W17).</summary>
    [Test]
    public void TheHashMovesWithLimitsAndOwnerSections()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.KitchenSink());
        var limits = CatalogSamples.KitchenSink();
        var ownerless = CatalogSamples.KitchenSink();
        var drone = ownerless.Archetypes[0];
        ownerless.Archetypes[0] = new CatalogArchetype { Name = drone.Name, Groups = drone.Groups, Position = drone.Position, Fields = drone.Fields };

        Assert.Multiple(() =>
        {
            Assert.That(CatalogSerializer.ComputeHash(new Catalog
            {
                Protocol = limits.Protocol, App = limits.App, Tick = limits.Tick, SessionKinds = limits.SessionKinds, Archetypes = limits.Archetypes,
                Enums = limits.Enums, Events = limits.Events, Commands = limits.Commands, Grids = limits.Grids, Metrics = limits.Metrics,
                Limits = new CatalogLimits { FrameBytes = 131_072, ClientMessageBytes = 1024, ResumeGraceMs = 60_000 },
            }), Is.Not.EqualTo(baseline));
            Assert.That(CatalogSerializer.ComputeHash(ownerless), Is.Not.EqualTo(baseline));
        });
    }

    [Test]
    public void TheHashTravelsAsEightLittleEndianBytes()
    {
        var bytes = new byte[8];
        CatalogSerializer.WriteHash(0x689b085895503760UL, bytes);

        Assert.Multiple(() =>
        {
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x60, 0x37, 0x50, 0x95, 0x58, 0x08, 0x9b, 0x68 }));
            Assert.That(CatalogSerializer.ToHex(0x689b085895503760UL), Is.EqualTo("689b085895503760"));
        });
    }
}
