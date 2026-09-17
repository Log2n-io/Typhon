using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The sample catalogs the tests and the golden vectors are built from.
/// </summary>
/// <remarks>
/// Shaped after the worked example in <c>claude/design/Subscriptions/03-wire-protocol.md § 4</c> rather than invented here, so the committed vectors exercise
/// the codecs a real application actually declares — a quantized position with motion, a bit-packed enum, a fraction, an enter-only field — instead of a
/// convenient subset.
/// </remarks>
internal static class CatalogSamples
{
    /// <summary>Builds the reference catalog, in deliberately non-alphabetical declaration order so canonicalization has something to do.</summary>
    /// <returns>A catalog equivalent to <see cref="SwgReordered"/> but declared in a different order.</returns>
    internal static Catalog Swg() => new()
    {
        Protocol = new CatalogProtocolVersion { Major = 2, Minor = 0 },
        App = new CatalogApp { Name = "SwgTatooine", Revision = 3 },
        Tick = new CatalogTick { PeriodUs = 100_000, PingHz = 4 },
        Archetypes =
        [
            new CatalogArchetype
            {
                Name = "Creature",
                Groups = ["enter", "motion", "state", "vitals"],
                Fields =
                [
                    new CatalogField { Name = "template", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "enter" },
                    new CatalogField
                    {
                        Name = "pos",
                        Codec = new CatalogCodec { Kind = CodecKind.Pos2, Min = [-8192, -8192], Max = [8192, 8192], Bits = 24 },
                        Group = "motion",
                        Motion = new CatalogMotion { Velocity = "vel", Model = "linear", Tolerance = 0.05, Discontinuity = "epoch" },
                    },
                    new CatalogField { Name = "vel", Codec = new CatalogCodec { Kind = CodecKind.Vel2, QuantaDiv = 16, Bits = 16 }, Group = "motion" },
                    new CatalogField { Name = "t0", Codec = new CatalogCodec { Kind = CodecKind.TickLo, FixedBytes = 2 }, Group = "motion" },
                    new CatalogField { Name = "epoch", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "motion" },
                    new CatalogField
                    {
                        Name = "mode", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 3, Pack = "st" }, Group = "state", Enum = "AiMode",
                    },
                    new CatalogField { Name = "hp", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 }, Group = "vitals", Smoothing = "snap" },
                ],
            },
            new CatalogArchetype
            {
                Name = "Player",
                Groups = ["enter", "motion", "state"],
                Fields =
                [
                    new CatalogField
                    {
                        Name = "pos",
                        Codec = new CatalogCodec { Kind = CodecKind.Pos2, Min = [-8192, -8192], Max = [8192, 8192], Bits = 24 },
                        Group = "motion",
                        Motion = new CatalogMotion { Velocity = "vel", Model = "linear", Tolerance = 0.05, Discontinuity = "epoch" },
                    },
                    new CatalogField { Name = "vel", Codec = new CatalogCodec { Kind = CodecKind.Vel2, QuantaDiv = 16, Bits = 16 }, Group = "motion" },
                    new CatalogField
                    {
                        Name = "activity", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 3, Pack = "st" }, Group = "state", Enum = "PlayerActivity",
                    },
                    new CatalogField { Name = "credits", Codec = new CatalogCodec { Kind = CodecKind.Varu }, Group = "enter" },
                ],
            },
        ],
        Enums = new Dictionary<string, string[]>
        {
            ["PlayerActivity"] = ["Standing", "Running", "Fighting"],
            ["AiMode"] = ["Idle", "Wander", "Pursue", "Fighting", "Leashing", "Dead"],
        },
        Events =
        [
            new CatalogEvent
            {
                Name = "Attack",
                Scope = "interest",
                Fields =
                [
                    new CatalogField { Name = "target", Codec = new CatalogCodec { Kind = CodecKind.EntityRef } },
                    new CatalogField { Name = "amount", Codec = new CatalogCodec { Kind = CodecKind.Varu } },
                ],
            },
        ],
        Commands =
        [
            new CatalogCommand
            {
                Name = "MoveTo",
                Delivery = "latest",
                Rate = new CatalogCommandRate { PerSec = 10, Burst = 20 },
                Fields =
                [
                    new CatalogField { Name = "dest", Codec = new CatalogCodec { Kind = CodecKind.Pos2, Min = [-8192, -8192], Max = [8192, 8192], Bits = 24 } },
                ],
            },
        ],
        Grids = [new CatalogGrid { Origin = [-8192, -8192], Cell = 256, Dims = [64, 64], Archetypes = [0, 1] }],
        Metrics = [new CatalogMetric { Name = "tick.p99", Unit = "ms", Codec = new CatalogCodec { Kind = CodecKind.F16 } }],
    };

    /// <summary>The same catalog with its archetypes, fields, enums and groups declared in a different order.</summary>
    /// <returns>A catalog that must canonicalize to exactly what <see cref="Swg"/> canonicalizes to.</returns>
    internal static Catalog SwgReordered()
    {
        var c = Swg();
        var archetypes = new[] { c.Archetypes[1], c.Archetypes[0] };
        var creature = archetypes[1];
        var shuffled = new CatalogField[creature.Fields.Length];
        for (var i = 0; i < creature.Fields.Length; i++)
        {
            shuffled[i] = creature.Fields[creature.Fields.Length - 1 - i];
        }

        archetypes[1] = new CatalogArchetype
        {
            Name = creature.Name,
            Groups = ["vitals", "state", "motion", "enter"],
            Fields = shuffled,
        };

        return new Catalog
        {
            Protocol = c.Protocol,
            App = c.App,
            Tick = c.Tick,
            Archetypes = archetypes,
            Enums = c.Enums,
            Events = c.Events,
            Commands = c.Commands,
            Grids = c.Grids,
            Metrics = c.Metrics,
        };
    }
}
