using System;
using System.Linq;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The registration rules of 03-wire-protocol § 12: each test breaks exactly one and asserts the validator names it. A rule no test breaks is a rule nobody
/// knows still holds.
/// </summary>
[TestFixture]
public class CatalogValidatorTests
{
    [Test]
    public void BothSamplesAreValid()
    {
        Assert.DoesNotThrow(() => CatalogValidator.Validate(CatalogSamples.Swg()));
        Assert.DoesNotThrow(() => CatalogValidator.Validate(CatalogSamples.KitchenSink()));
    }

    [Test]
    public void NineGroupsDoNotFitTheMask() =>
        AssertBreaks(c => WithCreature(c, a => new CatalogArchetype
        {
            Name = a.Name, Position = a.Position, Fields = a.Fields,
            Groups = ["state", "vitals", "g3", "g4", "g5", "g6", "g7", "g8", "g9"],
        }), "groups");

    [Test]
    public void AFieldMustSetExactlyOneOfGroupAndOnEnter() =>
        AssertBreaks(c => WithCreature(c, a => new CatalogArchetype
        {
            Name = a.Name, Groups = a.Groups, Position = a.Position,
            Fields = [.. a.Fields, new CatalogField { Name = "both", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "state", OnEnter = true }],
        }), "exactly one of group and onEnter");

    [Test]
    public void BitsWiderThan24AreRefused() =>
        AssertBreaks(c => WithCreatureField(c, "mode", new CatalogCodec { Kind = CodecKind.Bits, N = 25 }), "bits.n");

    [Test]
    public void QuantizingWidthsAre8To32InBytes() =>
        AssertBreaks(c => WithCreatureField(c, "hp", new CatalogCodec { Kind = CodecKind.Unorm, Bits = 12 }), "bits must be 8, 16, 24 or 32");

    [Test]
    public void AnEnumOnAFloatIsRefused() =>
        AssertBreaks(c => WithCreatureField(c, "mode", new CatalogCodec { Kind = CodecKind.F32 }), "enum is allowed only on");

    [Test]
    public void AnEnumThatOverflowsItsBitsIsRefused() =>
        AssertBreaks(c => WithCreatureField(c, "mode", new CatalogCodec { Kind = CodecKind.Bits, N = 2 }), "has 6 names");

    /// <summary>W2: a step below 2⁸ ulps of the bounds loses the round trip, so it is refused at registration rather than discovered in a client.</summary>
    [Test]
    public void AQuantStepTooFineForItsBoundsIsRefused() =>
        AssertBreaks(c => WithCreatureField(c, "hp", new CatalogCodec { Kind = CodecKind.Quant, Min = [1e9], Max = [1e9 + 16], Bits = 32 }), "round-trip");

    [Test]
    public void ALinearPositionNeedsAMatchingVelocity() =>
        AssertBreaks(c => WithCreature(c, a => new CatalogArchetype
        {
            Name = a.Name, Groups = a.Groups, Fields = a.Fields,
            Position = new CatalogPosition
            {
                Kind = CatalogPosition.MotionKind, Model = CatalogPosition.LinearModel, Pos = CatalogSamples.Pos2(),
                Vel = new CatalogCodec { Kind = CodecKind.Vel3, UnitExp = -13, Bits = 16 },
            },
        }), "matching pos");

    [Test]
    public void AVelocityOutsideAPositionIsRefused() =>
        AssertBreaks(c => WithCreatureField(c, "hp", new CatalogCodec { Kind = CodecKind.Vel2, UnitExp = 0, Bits = 16 }), "only valid inside a position");

    [Test]
    public void AListIsNotAnArchetypeField() =>
        AssertBreaks(
            c => WithCreatureField(c, "hp", new CatalogCodec { Kind = CodecKind.List, Of = new CatalogCodec { Kind = CodecKind.U8 }, MaxCount = 3 }),
            "list is only valid in event and command fields");

    [Test]
    public void AnUnknownCodecWithoutFixedBytesCannotBeSkipped() =>
        AssertBreaks(c => WithCreatureField(c, "hp", new CatalogCodec { Type = "future" }), "cannot be skipped");

    [Test]
    public void TheBuiltInMetricPrefixIsReserved() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, Archetypes = c.Archetypes, Enums = c.Enums, Events = c.Events,
            Commands = c.Commands, Grids = c.Grids,
            Metrics = [new CatalogMetric { Name = "typhon.mine", Unit = "x", Codec = new CatalogCodec { Kind = CodecKind.U8 } }],
        }, "prefix is reserved");

    [Test]
    public void LimitsAreRequired() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Archetypes = c.Archetypes, Enums = c.Enums, Events = c.Events, Commands = c.Commands,
            Grids = c.Grids, Metrics = c.Metrics,
        }, "limits");

    [Test]
    public void OwnerFieldsMustNameAnOwnerGroup()
    {
        AssertBreaks(c =>
        {
            var drone = c.Archetypes[0];
            c.Archetypes[0] = new CatalogArchetype
            {
                Name = drone.Name, Groups = drone.Groups, Position = drone.Position, Fields = drone.Fields,
                Owner = new CatalogOwner
                {
                    Groups = drone.Owner.Groups,
                    Fields = [new CatalogField { Name = "stray", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "vitals" }],
                },
            };

            return c;
        }, "owner groups", CatalogSamples.KitchenSink);
    }

    /// <summary>W27: a built-in is recognised by its shape, not its name — an application command named ClientRegion is refused.</summary>
    [Test]
    public void AnApplicationCommandCannotTakeABuiltInName() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, Archetypes = c.Archetypes, Enums = c.Enums, Events = c.Events,
            Grids = c.Grids,
            Metrics = c.Metrics,
            Commands =
            [
                new CatalogCommand
                {
                    Name = BuiltInCommands.ClientRegion, Delivery = CatalogCommand.QueuedDelivery,
                    Fields = [new CatalogField { Name = "x", Codec = new CatalogCodec { Kind = CodecKind.U8 } }],
                },
            ],
        }, "is a built-in");

    [Test]
    public void ABuiltInMetricMustKeepItsShape() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, Archetypes = c.Archetypes, Enums = c.Enums, Events = c.Events,
            Grids = c.Grids,
            Commands = c.Commands,
            Metrics = [new CatalogMetric { Name = "typhon.tick.p99", Unit = "ms", Codec = new CatalogCodec { Kind = CodecKind.U32 } }],
        }, "is a built-in");

    [Test]
    public void SubscribeRequestIsReservedUntilSourcesExist() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, Archetypes = c.Archetypes, Enums = c.Enums, Events = c.Events,
            Grids = c.Grids,
            Metrics = c.Metrics,
            Commands = [new CatalogCommand { Name = BuiltInCommands.SubscribeRequest, Delivery = CatalogCommand.QueuedDelivery, Fields = [] }],
        }, "is reserved");

    /// <summary>A parameter the kind does not read would be honoured by some other decoder: refused, so every decoder reads the same width.</summary>
    [Test]
    public void AParameterTheCodecDoesNotReadIsRefused() =>
        AssertBreaks(c => WithCreatureField(c, "hp", new CatalogCodec { Kind = CodecKind.U8, FixedBytes = 4 }), "does not read");

    [Test]
    public void AnInfiniteQuantRangeIsRefused() =>
        AssertBreaks(c => WithCreatureField(c, "hp", new CatalogCodec { Kind = CodecKind.Quant, Min = [-1e308], Max = [1e308], Bits = 16 }), "finite range");

    [Test]
    public void AListOfStringsIsRefused()
    {
        AssertBreaks(c =>
        {
            var e = c.Events[0];
            c.Events[0] = new CatalogEvent
            {
                Name = e.Name, Scope = e.Scope,
                Fields =
                [
                    .. e.Fields,
                    new CatalogField
                    {
                        Name = "names",
                        Codec = new CatalogCodec { Kind = CodecKind.List, Of = new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 8 }, MaxCount = 2 },
                    },
                ],
            };

            return c;
        }, "numeric byte-aligned");
    }

    /// <summary>A grid's tile is a whole number of replication cells, at least one (typhon.3): origin and dimensions are the realm frame's.</summary>
    [Test]
    public void AGridWithoutATileIsRefused() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, RealmKinds = c.RealmKinds, Archetypes = c.Archetypes, Enums = c.Enums,
            Events = c.Events, Commands = c.Commands, Metrics = c.Metrics,
            Grids = [new CatalogGrid { TileCells = 0, Archetypes = [0] }],
        }, "tileCells");

    /// <summary>A position codec is realm-framed (typhon.3, SUB-30): bits or bounds in the catalog would be a second, disagreeing frame.</summary>
    [Test]
    public void APositionCodecCarriesNoBitsOrBounds()
    {
        AssertBreaks(c => WithCreature(c, a => new CatalogArchetype
        {
            Name = a.Name, Groups = a.Groups, Fields = a.Fields,
            Position = new CatalogPosition
            {
                Kind = CatalogPosition.MotionKind, Model = CatalogPosition.LinearModel, Pos = new CatalogCodec { Kind = CodecKind.Pos2, Bits = 24 },
                Vel = new CatalogCodec { Kind = CodecKind.Vel2, UnitExp = -13, Bits = 16 },
            },
        }), "does not read");
        AssertBreaks(c => WithCreature(c, a => new CatalogArchetype
        {
            Name = a.Name, Groups = a.Groups, Fields = a.Fields,
            Position = new CatalogPosition { Kind = CatalogPosition.StaticKind, Pos = new CatalogCodec { Kind = CodecKind.Pos2, Min = [0, 0], Max = [1, 1] } },
        }), "does not read");
    }

    /// <summary>Each realm kind is declared once (typhon.3): a REALM block names its kind by index.</summary>
    [Test]
    public void RealmKindsAreDeclaredOnceEach()
    {
        AssertBreaks(c => WithRealmKinds(c, ["", "space", "space"]), "listed twice");
        Assert.That(() => CatalogValidator.Validate(WithRealmKinds(CatalogSamples.Swg(), null)), Throws.Nothing, "absent means the default kind alone");
    }

    private static Catalog WithRealmKinds(Catalog c, string[] kinds) => new()
    {
        Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, SessionKinds = c.SessionKinds, RealmKinds = kinds, Archetypes = c.Archetypes,
        Enums = c.Enums, Events = c.Events, Commands = c.Commands, Grids = c.Grids, Metrics = c.Metrics,
    };

    [Test]
    public void AMetricNeedsAUnit() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, Archetypes = c.Archetypes, Enums = c.Enums, Events = c.Events,
            Commands = c.Commands, Grids = c.Grids,
            Metrics = [new CatalogMetric { Name = "app.depth", Codec = new CatalogCodec { Kind = CodecKind.U8 } }],
        }, "unit is missing");

    [Test]
    public void AnEnumNeedsItsNames()
    {
        AssertBreaks(c =>
        {
            c.Enums["Empty"] = null;
            return c;
        }, "has no names");
    }

    /// <summary>Two grids of one tile over one archetype set are one grid.</summary>
    [Test]
    public void AGridDuplicateIsRefused() =>
        AssertBreaks(c => new Catalog
        {
            Protocol = c.Protocol, App = c.App, Tick = c.Tick, Limits = c.Limits, RealmKinds = c.RealmKinds, Archetypes = c.Archetypes, Enums = c.Enums,
            Events = c.Events, Commands = c.Commands, Metrics = c.Metrics,
            Grids =
            [
                new CatalogGrid { TileCells = 4, Archetypes = [0] },
                new CatalogGrid { TileCells = 4, Archetypes = [0] },
            ],
        }, "duplicates grid");

    private static void AssertBreaks(Func<Catalog, Catalog> mutate, string expectedFragment, Func<Catalog> sample = null)
    {
        var broken = mutate((sample ?? CatalogSamples.Swg)());
        var ex = Assert.Throws<CatalogException>(() => CatalogValidator.Validate(broken));
        Assert.That(ex.Problems.Any(p => p.Contains(expectedFragment, StringComparison.Ordinal)), Is.True,
            $"expected a problem mentioning '{expectedFragment}', got:{Environment.NewLine}{ex.Message}");
    }

    private static Catalog WithCreature(Catalog c, Func<CatalogArchetype, CatalogArchetype> change)
    {
        var i = Array.FindIndex(c.Archetypes, a => a.Name == "Creature");
        c.Archetypes[i] = change(c.Archetypes[i]);
        return c;
    }

    private static Catalog WithCreatureField(Catalog c, string field, CatalogCodec codec) =>
        WithCreature(c, a => new CatalogArchetype
        {
            Name = a.Name, Groups = a.Groups, Position = a.Position,
            Fields = a.Fields.Select(f => f.Name == field
                ? new CatalogField { Name = f.Name, Codec = codec, Group = f.Group, OnEnter = f.OnEnter, Enum = f.Enum, Smoothing = f.Smoothing }
                : f).ToArray(),
        });
}
