using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The sample catalogs the tests and the golden vectors are built from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Swg"/> is shaped after the worked example in <c>claude/design/Subscriptions/03-wire-protocol.md § 4</c> rather than invented here, so the
/// committed vectors exercise the codecs a real application declares — a quantized moving position, a bit-packed enum, a fraction, an enter-only field.
/// </para>
/// <para>
/// <see cref="KitchenSink"/> exists for the opposite reason: it is nobody's application. It declares every codec, every position kind, an owner section,
/// labelled and session metrics, and a built-in command, so a decoder that passes its vectors has been shown every construct the wire has.
/// </para>
/// </remarks>
internal static class CatalogSamples
{
    internal static readonly double[] WorldMin = [-8192, -8192];
    internal static readonly double[] WorldMax = [8192, 8192];

    internal static CatalogCodec Pos2() => new() { Kind = CodecKind.Pos2, Min = WorldMin, Max = WorldMax, Bits = 24 };

    /// <summary>Builds the reference catalog, in deliberately non-canonical declaration order so canonicalization has something to do.</summary>
    /// <returns>A catalog equivalent to <see cref="SwgReordered"/> but declared in a different order.</returns>
    internal static Catalog Swg() => new()
    {
        Protocol = new CatalogProtocolVersion { Major = 3, Minor = 0 },
        App = new CatalogApp { Name = "SwgTatooine", Revision = 3 },
        Tick = new CatalogTick { PeriodUs = 100_000, PingHz = 4 },
        Limits = new CatalogLimits { FrameBytes = 262_144, ClientMessageBytes = 1024, ResumeGraceMs = 60_000 },
        SessionKinds = ["player", "god"],
        Archetypes =
        [
            new CatalogArchetype
            {
                Name = "Player",
                Groups = ["state"],
                Position = MovingLinear(),
                Fields =
                [
                    new CatalogField { Name = "activity", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 3 }, Group = "state", Enum = "PlayerActivity" },
                    new CatalogField { Name = "credits", Codec = new CatalogCodec { Kind = CodecKind.Varu }, OnEnter = true },
                ],
            },
            new CatalogArchetype
            {
                Name = "Creature",
                Groups = ["vitals", "state"],
                Position = MovingLinear(),
                Fields =
                [
                    new CatalogField { Name = "template", Codec = new CatalogCodec { Kind = CodecKind.U8 }, OnEnter = true },
                    new CatalogField { Name = "mode", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 3 }, Group = "state", Enum = "AiMode" },
                    new CatalogField { Name = "hp", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 }, Group = "vitals", Smoothing = "snap" },
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
                Delivery = CatalogCommand.LatestDelivery,
                Rate = new CatalogCommandRate { PerSec = 10, Burst = 20 },
                Fields = [new CatalogField { Name = "dest", Codec = Pos2() }],
            },
            BuiltInCommands.CreateClientRegion(Pos2()),
        ],
        Grids = [new CatalogGrid { Origin = [-8192, -8192], Cell = 256, Dims = [64, 64], Archetypes = [0, 1] }],
        Metrics = [BuiltInMetrics.Create(BuiltInMetrics.ReservedIdx("typhon.tick.p99"))],
    };

    /// <summary>The same catalog with its archetypes, fields, groups, commands and session kinds declared in a different order.</summary>
    /// <returns>A catalog that must canonicalize to exactly what <see cref="Swg"/> canonicalizes to.</returns>
    internal static Catalog SwgReordered()
    {
        var c = Swg();
        var creature = c.Archetypes[1];
        var fields = (CatalogField[])creature.Fields.Clone();
        System.Array.Reverse(fields);
        return new Catalog
        {
            Protocol = c.Protocol,
            App = c.App,
            Tick = c.Tick,
            Limits = c.Limits,
            SessionKinds = ["god", "player"],
            Archetypes =
            [
                new CatalogArchetype { Name = creature.Name, Groups = ["state", "vitals"], Position = creature.Position, Fields = fields },
                c.Archetypes[0],
            ],
            Enums = c.Enums,
            Events = c.Events,
            Commands = [c.Commands[1], c.Commands[0]],
            Grids = [new CatalogGrid { Origin = [-8192, -8192], Cell = 256, Dims = [64, 64], Archetypes = [1, 0] }],
            Metrics = c.Metrics,
        };
    }

    /// <summary>Every construct the wire has, in one catalog.</summary>
    /// <returns>The kitchen-sink catalog.</returns>
    internal static Catalog KitchenSink() => new()
    {
        Protocol = new CatalogProtocolVersion { Major = 3, Minor = 0 },
        App = new CatalogApp { Name = "KitchenSink", Revision = 1 },
        Tick = new CatalogTick { PeriodUs = 50_000, PingHz = 4 },
        Limits = new CatalogLimits { FrameBytes = 262_144, ClientMessageBytes = 1024, ResumeGraceMs = 60_000 },
        SessionKinds = ["viewer"],
        Archetypes =
        [
            new CatalogArchetype
            {
                Name = "Drone",
                Groups = ["flags", "motion2", "vitals"],
                Position = new CatalogPosition
                {
                    Kind = CatalogPosition.MotionKind,
                    Model = CatalogPosition.LinearModel,
                    Pos = new CatalogCodec { Kind = CodecKind.Pos3, Min = [-1024, -64, -1024], Max = [1024, 64, 1024], Bits = 16 },
                    Vel = new CatalogCodec { Kind = CodecKind.Vel3, UnitExp = -7, Bits = 8 },
                },
                Fields =
                [
                    new CatalogField { Name = "serial", Codec = new CatalogCodec { Kind = CodecKind.Bytes, N = 4 }, OnEnter = true },
                    new CatalogField { Name = "label", Codec = new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 32 }, OnEnter = true },
                    new CatalogField { Name = "armed", Codec = new CatalogCodec { Kind = CodecKind.Bool }, Group = "flags" },
                    new CatalogField { Name = "lights", Codec = new CatalogCodec { Kind = CodecKind.Bool }, Group = "flags" },
                    new CatalogField { Name = "stance", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 6 }, Group = "flags", Enum = "Stance" },
                    new CatalogField { Name = "rotation", Codec = new CatalogCodec { Kind = CodecKind.Quat3 }, Group = "motion2" },
                    new CatalogField { Name = "heading", Codec = new CatalogCodec { Kind = CodecKind.Angle, Bits = 16 }, Group = "motion2" },
                    new CatalogField { Name = "thrust", Codec = new CatalogCodec { Kind = CodecKind.Vec3, Scale = 0.5, Bits = 8 }, Group = "motion2" },
                    new CatalogField { Name = "battery", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 16 }, Group = "vitals" },
                    new CatalogField { Name = "tilt", Codec = new CatalogCodec { Kind = CodecKind.Snorm, Bits = 8 }, Group = "vitals" },
                    new CatalogField
                    {
                        Name = "temperature", Codec = new CatalogCodec { Kind = CodecKind.Quant, Min = [-40], Max = [88], Bits = 8 }, Group = "vitals",
                    },
                    new CatalogField { Name = "lastHit", Codec = new CatalogCodec { Kind = CodecKind.TickLo }, Group = "vitals" },
                    new CatalogField { Name = "target", Codec = new CatalogCodec { Kind = CodecKind.EntityRef }, Group = "vitals" },
                ],
                Owner = new CatalogOwner
                {
                    Groups = ["cargo", "secrets"],
                    Fields =
                    [
                        new CatalogField { Name = "manifest", Codec = new CatalogCodec { Kind = CodecKind.Blob, MaxBytes = 64 }, Group = "cargo" },
                        new CatalogField { Name = "fuel", Codec = new CatalogCodec { Kind = CodecKind.F32 }, Group = "cargo" },
                        new CatalogField { Name = "pin", Codec = new CatalogCodec { Kind = CodecKind.U16 }, Group = "secrets" },
                        new CatalogField { Name = "vault", Codec = new CatalogCodec { Kind = CodecKind.Bool }, Group = "secrets" },
                    ],
                },
            },
            new CatalogArchetype
            {
                Name = "Beacon",
                Groups = ["signal"],
                Position = new CatalogPosition { Kind = CatalogPosition.StaticKind, Pos = Pos2() },
                Fields =
                [
                    new CatalogField { Name = "channel", Codec = new CatalogCodec { Kind = CodecKind.U8 }, OnEnter = true },
                    new CatalogField { Name = "strength", Codec = new CatalogCodec { Kind = CodecKind.F16 }, Group = "signal" },
                    new CatalogField { Name = "drift", Codec = new CatalogCodec { Kind = CodecKind.Vari }, Group = "signal" },
                ],
            },
            new CatalogArchetype
            {
                Name = "Buoy",
                Groups = ["sample"],
                Position = new CatalogPosition { Kind = CatalogPosition.MotionKind, Model = CatalogPosition.NoneModel, Pos = Pos2() },
                Fields =
                [
                    new CatalogField { Name = "depth", Codec = new CatalogCodec { Kind = CodecKind.I16 }, Group = "sample" },
                    new CatalogField { Name = "reading", Codec = new CatalogCodec { Kind = CodecKind.I32 }, Group = "sample" },
                ],
            },
            new CatalogArchetype
            {
                Name = "Ledger",
                Groups = ["balance"],
                Fields =
                [
                    new CatalogField { Name = "owner", Codec = new CatalogCodec { Kind = CodecKind.U32 }, OnEnter = true },
                    new CatalogField { Name = "amount", Codec = new CatalogCodec { Kind = CodecKind.I8 }, Group = "balance" },
                    new CatalogField { Name = "future", Codec = new CatalogCodec { Type = "future", FixedBytes = 3 }, Group = "balance" },
                ],
            },
        ],
        Enums = new Dictionary<string, string[]> { ["Stance"] = ["Idle", "Patrol", "Engage"] },
        Events =
        [
            new CatalogEvent
            {
                Name = "Ping",
                Scope = "interest",
                Fields =
                [
                    new CatalogField { Name = "from", Codec = new CatalogCodec { Kind = CodecKind.EntityRef } },
                    new CatalogField { Name = "path", Codec = new CatalogCodec { Kind = CodecKind.List, Of = Pos2(), MinCount = 0, MaxCount = 4 } },
                    new CatalogField { Name = "loud", Codec = new CatalogCodec { Kind = CodecKind.Bool } },
                ],
            },
            new CatalogEvent
            {
                Name = "Chat",
                Scope = "known",
                Fields =
                [
                    new CatalogField { Name = "text", Codec = new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 128 } },
                    new CatalogField { Name = "attachment", Codec = new CatalogCodec { Kind = CodecKind.Blob, MaxBytes = 32 } },
                ],
            },
        ],
        Commands =
        [
            BuiltInCommands.CreateClientRegion(Pos2()),
            new CatalogCommand
            {
                Name = "Steer",
                Delivery = CatalogCommand.QueuedDelivery,
                Fields =
                [
                    new CatalogField { Name = "heading", Codec = new CatalogCodec { Kind = CodecKind.Angle, Bits = 8 } },
                    new CatalogField { Name = "boost", Codec = new CatalogCodec { Kind = CodecKind.Bool } },
                    new CatalogField { Name = "speed", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 } },
                    new CatalogField { Name = "stance", Codec = new CatalogCodec { Kind = CodecKind.Bits, N = 2 }, Enum = "Stance" },
                    new CatalogField { Name = "note", Codec = new CatalogCodec { Kind = CodecKind.Str, MaxBytes = 16 } },
                ],
            },
        ],
        Grids = [new CatalogGrid { Origin = [-8192, -8192], Cell = 1024, Dims = [16, 16], Archetypes = [0, 2] }],
        Metrics =
        [
            BuiltInMetrics.Create(BuiltInMetrics.ReservedIdx("typhon.tick.p50")),
            BuiltInMetrics.Create(BuiltInMetrics.ReservedIdx(BuiltInMetrics.SystemMean), ["Physics", "Ai", "Net"]),
            BuiltInMetrics.Create(BuiltInMetrics.ReservedIdx("typhon.session.skippedFrames")),
            new CatalogMetric { Name = "app.load", Unit = "ratio", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 } },
            new CatalogMetric
            {
                Name = "app.queue", Unit = "count", Codec = new CatalogCodec { Kind = CodecKind.U16 }, Scope = CatalogMetric.SessionScope,
            },
        ],
    };

    private static CatalogPosition MovingLinear() => new()
    {
        Kind = CatalogPosition.MotionKind,
        Model = CatalogPosition.LinearModel,
        Pos = Pos2(),
        Vel = new CatalogCodec { Kind = CodecKind.Vel2, UnitExp = -13, Bits = 16 },
    };
}
