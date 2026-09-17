using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-03 — what the engine puts on the wire as a catalog: everything a client needs to decode a stream, nothing about the server's memory, and the same bytes
/// every time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scan is the point of this fixture</b> (foundation/06 § 4 criterion 4). "No CLR type name and no byte offset appears in the catalog" is a property of
/// the OUTPUT, and reviewing the builder for it proves nothing about the next field somebody adds — so it is asserted against the emitted bytes, three ways:
/// a forbidden-substring scan, a closed whitelist of property names, and a sweep for the plan's own offsets as numbers.
/// </para>
/// <para>
/// <b>What "no CLR type name" can and cannot mean.</b> An archetype, a command and an event travel under the name the projection declared, and that name
/// DEFAULTS to the type's simple name — so <c>ProjCreature</c> is in the bytes because the application put it there. What must never be there is a name the
/// application did not publish: the component types a projection reads through, a source field whose wire name was overridden, a namespace, an assembly
/// identity. That is what is scanned for.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CatalogBuilderTests : TestBase<CatalogBuilderTests>
{
    /// <summary>The vector this fixture regenerates under <c>TYPHON_UPDATE_GOLDEN=1</c>, and the one the TypeScript SDK reads back.</summary>
    private const string GoldenCase = "catalog-engine";

    private const string GoldenDescription =
        "The engine's own catalog, emitted by CatalogBuilder from a compiled projection plan rather than hand-built: three archetypes (one moving with a "
        + "packed enum and a fraction, one with an owner section, one static), the eleven built-in metrics, an application metric, an event, two commands and "
        + "the built-in ClientRegion at index 0.";

    /// <summary>The nominal tick period the vector declares: 10 Hz, as the demo's catalog does.</summary>
    private const int TickPeriodUs = 100_000;

    /// <summary>The largest allowed tick multiplier the velocity codec is sized against — the ladder's ceiling, which is 6.</summary>
    private const int LargestTickMultiplier = 6;

    private static readonly string[] SystemNames = ["Movement", "Combat", "Spawning"];

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    // ── The declarations the fixture emits from ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything the golden vector carries, declared through the public API only.</summary>
    private static void DeclareAll(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god", "player");
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareRock(subs);
        DeclareMessages(subs);
    }

    /// <summary>The same contract with every archetype declared in the opposite order, and the creature's fields reversed.</summary>
    private static void DeclareAllReordered(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("player", "god");
        ProjectionTestSchema.DeclareRock(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareCreatureReversed(subs);
        DeclareMessages(subs);
    }

    private static void DeclareMessages(SubscriptionsRegistry subs)
    {
        // A ClientRegion observer is what enables the built-in command (W27). Phase 1 refuses the observer itself at Start — the catalog is built from the
        // declarations, not from what the interest pass can serve yet, so the command's reserved index is testable now and the observer is not.
        subs.Profile("god-world", p => p.World().Of<ProjCreature>().Of<ProjPlayer>().Of<ProjRock>());
        subs.Profile("region", p => p.ClientRegion(maxEdgeM: 512));

        var attacks = new EventQueue<SwgAttack>("Attacks", 64);
        subs.Event(attacks, e => e
            .RouteToKnown(a => a.Target, a => a.Attacker)
            .Entity(a => a.Attacker)
            .Entity(a => a.Target)
            .Field(a => a.Damage, Codec.U16));

        subs.Command<SwgMoveTo>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(10, burst: 20)
            .Field(m => m.X, Codec.Quant(-8192, 8192, 24))
            .Field(m => m.Z, Codec.Quant(-8192, 8192, 24)));

        subs.Command<SwgSetTarget>(c => c.Field(t => t.Target, Codec.EntityRef));

        subs.Metric("proj.creatures.alive", "count", Codec.VarUInt, static () => 0d);
    }

    private static CatalogExport Build(DatabaseEngine dbe, Action<SubscriptionsRegistry> declare) => BuildBoth(dbe, declare).Export;

    /// <summary>The catalog and the plan it was emitted from, so a test can assert the one carries what the other computed.</summary>
    private static (CatalogExport Export, CompiledProjectionPlan[] Plans) BuildBoth(DatabaseEngine dbe, Action<SubscriptionsRegistry> declare)
    {
        var subs = new SubscriptionsRegistry();
        declare(subs);
        var plans = ProjectionCompiler.Compile(subs, dbe, ProjectionTestSchema.TickPeriodSeconds, LargestTickMultiplier);
        return (CatalogBuilder.Build(subs, plans, CatalogBuilder.DefaultAppName, appRevision: 0, TickPeriodUs, SystemNames), plans);
    }

    // ── Validity ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The emitted catalog is one the engine's own validator accepts and one a client accepts: valid, and canonical to the byte.
    /// </summary>
    /// <remarks>
    /// <see cref="CatalogSerializer.FromUtf8"/> is the client's check, not a second serialization test: it refuses a catalog that is valid but whose indices
    /// or order are not the ones canonicalization assigns, because a decode plan built from that maps mask bits to the wrong fields.
    /// </remarks>
    [Test]
    public void TheEmittedCatalogIsValidAndCanonical()
    {
        using var dbe = SetupEngine();
        var export = Build(dbe, DeclareAll);

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => CatalogValidator.Validate(export.Canonical), "the emitted catalog breaks a wire rule");
            Assert.DoesNotThrow(() => CatalogSerializer.FromUtf8(export.Utf8), "a client would refuse the emitted bytes");
            Assert.That(export.Hash, Is.EqualTo(CatalogSerializer.HashBytes(export.Utf8)), "the digest is the FNV-1a of the bytes that travel, nothing else");
            Assert.That(export.Hash, Is.Not.Zero, "a catalog that digests to zero is always re-sent");
        });
    }

    /// <summary>
    /// Every archetype the projection declared reaches the catalog with its groups, its position and its fields in wire order — and its owner section apart.
    /// </summary>
    [Test]
    public void EveryDeclaredArchetypeReachesTheCatalog()
    {
        using var dbe = SetupEngine();
        var (export, plans) = BuildBoth(dbe, DeclareAll);
        var catalog = export.Canonical;
        var creaturePlan = ProjectionTestSchema.PlanFor(plans, nameof(ProjCreature));

        var creature = ArchetypeNamed(catalog, nameof(ProjCreature));
        var player = ArchetypeNamed(catalog, nameof(ProjPlayer));
        var rock = ArchetypeNamed(catalog, nameof(ProjRock));

        Assert.Multiple(() =>
        {
            // Ordinal by name, and the index follows the order — never declaration order, which DeclareAllReordered reverses.
            Assert.That(Names(catalog.Archetypes), Is.EqualTo(new[] { nameof(ProjCreature), nameof(ProjPlayer), nameof(ProjRock) }));
            Assert.That(creature.Idx, Is.EqualTo(0));
            Assert.That(rock.Idx, Is.EqualTo(2));

            Assert.That(creature.Groups, Is.EqualTo(new[] { "state", "vitals" }), "groups canonicalize ordinally, which is what a mask bit means");
            Assert.That(FieldNames(creature.Fields), Is.EqualTo(new[] { "template", "alerted", "mode", "hp", "level" }),
                "onEnter section first, then each group's, packed fields leading a section (W11)");
            Assert.That(FieldNamed(creature, "template").OnEnter, Is.True);
            Assert.That(FieldNamed(creature, "mode").Group, Is.EqualTo("state"));
            Assert.That(FieldNamed(creature, "mode").Enum, Is.EqualTo(nameof(ProjAiMode)), "a packed integer carries its value set as an attribute (W13)");
            Assert.That(catalog.Enums[nameof(ProjAiMode)], Is.EqualTo(Enum.GetNames<ProjAiMode>()));

            Assert.That(creature.Position.Kind, Is.EqualTo(CatalogPosition.MotionKind));
            Assert.That(creature.Position.Model, Is.EqualTo(CatalogPosition.LinearModel));
            Assert.That(creature.Position.Pos.Kind, Is.EqualTo(CodecKind.Pos2));
            Assert.That(creature.Position.Pos.Bits, Is.EqualTo(Codec.DefaultPositionBits));
            Assert.That(creature.Position.Vel.Kind, Is.EqualTo(CodecKind.Vel2));

            // The same objects the compiler derived, not a second derivation of the same numbers: what the catalog says IS what the encoder was built with.
            Assert.That(creature.Position.Pos, Is.SameAs(creaturePlan.Position.Pos));
            Assert.That(creature.Position.Vel, Is.SameAs(creaturePlan.Position.Vel));
            Assert.That(creature.Owner, Is.Null);

            Assert.That(player.Owner, Is.Not.Null, "an owner section travels apart, with its own groups and its own bit space (W17)");
            Assert.That(player.Owner.Groups, Is.EqualTo(new[] { "bag", "owner" }));
            Assert.That(FieldNames(player.Owner.Fields), Is.EqualTo(new[] { "items", "credits" }));

            // A static archetype is sent once on enter and never updated, so it has no change group at all and every field is an onEnter field.
            Assert.That(rock.Groups, Is.Empty);
            Assert.That(rock.Position.Kind, Is.EqualTo(CatalogPosition.StaticKind));
            Assert.That(rock.Position.Model, Is.Null);
            Assert.That(rock.Position.Vel, Is.Null);
            Assert.That(FieldNamed(rock, "kind").OnEnter, Is.True);
        });
    }

    /// <summary>
    /// Built-in and application entries share the catalog without sharing a numbering: <c>ClientRegion</c> stays at 0 and the eleven built-in metrics stay
    /// at 0-10, whatever an application declares (W27).
    /// </summary>
    [Test]
    public void ReservedIndexRangesAreHonoured()
    {
        using var dbe = SetupEngine();
        var catalog = Build(dbe, DeclareAll).Canonical;

        Assert.Multiple(() =>
        {
            Assert.That(catalog.Commands[0].Name, Is.EqualTo(BuiltInCommands.ClientRegion));
            Assert.That(catalog.Commands[0].Idx, Is.EqualTo(BuiltInCommands.ClientRegionIdx));
            Assert.That(BuiltInCommands.HasClientRegionShape(catalog.Commands[0]), Is.True,
                "the engine interprets this command itself, so it has to be the engine's shape and not merely its name");

            // The region's vertices quantize with the world's own position codec, so a decoded footprint can never leave the world (W28).
            var vertices = Array.Find(catalog.Commands[0].Fields, f => f.Name == BuiltInCommands.RegionVerticesField);
            var creaturePos = ArchetypeNamed(catalog, nameof(ProjCreature)).Position.Pos;
            Assert.That(vertices.Codec.Of.Min, Is.EqualTo(creaturePos.Min));
            Assert.That(vertices.Codec.Of.Bits, Is.EqualTo(creaturePos.Bits));

            Assert.That(catalog.Commands[1].Idx, Is.EqualTo(ProtocolConstants.FirstAppCommandIdx), "application commands start above the reserved range");
            Assert.That(catalog.Commands[1].Name, Is.EqualTo(nameof(SwgMoveTo)));
            Assert.That(catalog.Commands[2].Idx, Is.EqualTo(ProtocolConstants.FirstAppCommandIdx + 1));

            Assert.That(catalog.Events, Has.Length.EqualTo(1));
            Assert.That(catalog.Events[0].Idx, Is.EqualTo(ProtocolConstants.FirstAppEventIdx));
            Assert.That(catalog.Events[0].Scope, Is.EqualTo("known"));

            Assert.That(catalog.Metrics, Has.Length.EqualTo(BuiltInMetrics.Count + 1));
            for (var idx = 0; idx < BuiltInMetrics.Count; idx++)
            {
                Assert.That(catalog.Metrics[idx].Idx, Is.EqualTo(idx));
                Assert.That(catalog.Metrics[idx].Name, Does.StartWith(ProtocolConstants.BuiltInMetricPrefix));
                Assert.That(BuiltInMetrics.HasShape(idx, catalog.Metrics[idx]), Is.True, $"built-in metric {idx} is not the engine's own shape");
            }

            var lastMetric = catalog.Metrics[^1];
            Assert.That(lastMetric.Idx, Is.EqualTo(ProtocolConstants.FirstAppMetricIdx), "application metrics start above the reserved range");
            Assert.That(lastMetric.Name, Is.EqualTo("proj.creatures.alive"));

            // The two vector built-ins carry one label per value, in the order the values travel; the label order is data and is never sorted.
            Assert.That(MetricNamed(catalog, BuiltInMetrics.SystemMean).Labels, Is.EqualTo(SystemNames));
            Assert.That(MetricNamed(catalog, BuiltInMetrics.ArchetypeEntities).Labels,
                Is.EqualTo(new[] { nameof(ProjCreature), nameof(ProjPlayer), nameof(ProjRock) }));
        });
    }

    /// <summary>A profile declaring no <c>ClientRegion</c> observer gets no such command: a built-in is listed only when enabled (W27).</summary>
    [Test]
    public void ABuiltInCommandIsListedOnlyWhenAProfileEnablesIt()
    {
        using var dbe = SetupEngine();
        var catalog = Build(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god-world", p => p.World().Of<ProjCreature>());
            subs.Command<SwgSetTarget>(c => c.Field(t => t.Target, Codec.EntityRef));
        }).Canonical;

        Assert.Multiple(() =>
        {
            Assert.That(Names(catalog.Commands), Is.EqualTo(new[] { nameof(SwgSetTarget) }));
            Assert.That(catalog.Commands[0].Idx, Is.EqualTo(ProtocolConstants.FirstAppCommandIdx),
                "the application's index does not move because a built-in was left out");
        });
    }

    // ── Nothing about server memory ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The emitted bytes name no component type, no namespace, no assembly and no source field the projection renamed (foundation/06 D3).
    /// </summary>
    [Test]
    public void NoClrTypeNameThePlanReadsThroughReachesTheBytes()
    {
        using var dbe = SetupEngine();
        var text = Encoding.UTF8.GetString(Build(dbe, DeclareAll).Utf8);

        // The component types the projections read through, as whole JSON tokens: `ProjAiMode` is legitimately present as the declared enum's name, and a
        // bare substring scan for `ProjAi` would match it. A leaked component name would be a key or a value, so quoting is what distinguishes the two.
        string[] forbiddenTokens =
        [
            $"\"{nameof(ProjBounds)}\"", $"\"{nameof(ProjAi)}\"", $"\"{nameof(ProjVitals)}\"", $"\"{nameof(ProjWallet)}\"",
        ];

        // The schema ids, the namespaces and the assembly-identity tokens an AssemblyQualifiedName carries: no form of these is ever legitimate.
        string[] forbidden = ["Typhon.Test.Proj", "Typhon.Engine", "Typhon.Protocol", "System.", "Archetype<", "Comp<", ".dll", "Version=", "Culture=",
            "PublicKeyToken",
        ];

        // The source fields whose wire name differs from the C# member's: the member name must not appear, or the catalog would be naming storage. Quoted for
        // the same reason as the component names — `Mode` is a substring of the declared enum `ProjAiMode`, which the application did publish.
        string[] renamedSources = Array.ConvertAll(
            ["Bounds", "Speed", "Template", "Mode", "Alerted", "Level", "Health", "MaxHealth", "Credits", "ItemCount", "ThinkCooldown"],
            static member => $"\"{member}\"");

        Assert.Multiple(() =>
        {
            foreach (var token in forbiddenTokens)
            {
                Assert.That(text, Does.Not.Contain(token), $"{token} names a component type the projection reads through, which no client decodes");
            }

            foreach (var token in forbidden)
            {
                Assert.That(text, Does.Not.Contain(token), $"'{token}' names a CLR construct and has no business on the wire");
            }

            foreach (var member in renamedSources)
            {
                Assert.That(text, Does.Not.Contain(member), $"'{member}' is a C# member name the projection renamed; the catalog carries the wire name");
            }
        });
    }

    /// <summary>
    /// Every property name in the emitted JSON belongs to the catalog model, so nothing the model does not define — an offset, a slot, a stride — can have
    /// been added to it without this failing.
    /// </summary>
    [Test]
    public void TheEmittedJsonCarriesOnlyCatalogProperties()
    {
        using var dbe = SetupEngine();
        var root = JsonNode.Parse(Build(dbe, DeclareAll).Utf8);

        // The closed set the catalog model defines, camelCased as the serializer writes it. An enum's KEYS are data, not properties, and are skipped below.
        HashSet<string> allowed =
        [
            "protocol", "major", "minor", "app", "name", "revision", "tick", "periodUs", "pingHz",
            "limits", "frameBytes", "clientMessageBytes", "resumeGraceMs", "sessionKinds",
            "archetypes", "idx", "groups", "position", "kind", "model", "pos", "vel", "fields", "codec", "group", "onEnter", "enum", "smoothing", "owner",
            "enums", "events", "scope", "commands", "delivery", "rate", "perSec", "burst",
            "grids", "origin", "cell", "dims", "archetypeIdx", "metrics", "unit", "labels",
            "t", "bits", "min", "max", "scale", "quantaDiv", "n", "maxBytes", "of", "minCount", "maxCount", "fixedBytes",
        ];

        var unexpected = new List<string>();
        WalkProperties(root, "", allowed, unexpected);
        Assert.That(unexpected, Is.Empty, "the catalog carries a property the model does not define");
    }

    /// <summary>
    /// None of the plan's storage numbers appears in the catalog: not a column offset, not a stride, not a field offset, not the cluster's slot count
    /// (foundation/06 D2).
    /// </summary>
    /// <remarks>
    /// Small numbers are excluded, and deliberately: a field offset of 0 or 8 is indistinguishable from a codec width or an index, so asserting on those
    /// would be asserting on a coincidence. What the projection fixture produces are column offsets in the thousands and strides in the tens — numbers that
    /// have no legitimate reason to appear, which is what makes their absence evidence.
    /// </remarks>
    [Test]
    public void NoStorageOffsetReachesTheBytes()
    {
        using var dbe = SetupEngine();
        var subs = new SubscriptionsRegistry();
        DeclareAll(subs);
        var plans = ProjectionCompiler.Compile(subs, dbe, ProjectionTestSchema.TickPeriodSeconds, LargestTickMultiplier);
        var export = CatalogBuilder.Build(subs, plans, CatalogBuilder.DefaultAppName, appRevision: 0, TickPeriodUs, SystemNames);

        var offsets = new HashSet<double>();
        foreach (var plan in plans)
        {
            offsets.Add(plan.SlotCount);
            offsets.Add(plan.BlockLayout.BlockSize);
            offsets.Add(plan.BlockLayout.ColdOffset);
            CollectOffsets(plan.Fields, offsets);
            CollectOffsets(plan.OwnerFields, offsets);
            if (plan.Position != null)
            {
                offsets.Add(plan.Position.ComponentOffsetInCluster);
                offsets.Add(plan.Position.ComponentSize);
                offsets.Add(plan.Position.FieldOffsetInComponent);
            }
        }

        offsets.RemoveWhere(static v => Math.Abs(v) <= 32);
        Assert.That(offsets, Is.Not.Empty, "the fixture must produce at least one distinctive offset, or this test asserts nothing");

        var numbers = new List<double>();
        CollectNumbers(JsonNode.Parse(export.Utf8), numbers);
        foreach (var number in numbers)
        {
            Assert.That(offsets, Does.Not.Contain(number), $"{number.ToString(CultureInfo.InvariantCulture)} is one of the plan's storage numbers");
        }
    }

    /// <summary>
    /// The catalog path names none of the four runtime code-generation APIs #409 rules out, in the builder or in the protocol library it emits through.
    /// </summary>
    /// <remarks>
    /// A source scan, which is what can be asserted cheaply and honestly: an IL walk would have to follow every call the serializer makes into
    /// <c>System.Text.Json</c>, whose source-generated context is the very thing that keeps this path AOT-clean. What this catches is the change that would
    /// reintroduce one — somebody reaching for <c>Expression.Compile</c> to read a declaration.
    /// </remarks>
    [Test]
    public void TheCatalogPathNamesNoRuntimeCodeGeneration()
    {
        string[] files =
        [
            "src/Typhon.Engine/Subscriptions/internals/CatalogBuilder.cs",
            "src/Typhon.Engine/Subscriptions/internals/SubscriptionsRuntime.cs",
            "src/Typhon.Protocol/Catalog/Catalog.cs",
            "src/Typhon.Protocol/Catalog/CatalogArchetype.cs",
            "src/Typhon.Protocol/Catalog/CatalogCodec.cs",
            "src/Typhon.Protocol/Catalog/CatalogMessages.cs",
            "src/Typhon.Protocol/Catalog/CatalogSerializer.cs",
            "src/Typhon.Protocol/Catalog/CatalogValidator.cs",
            "src/Typhon.Protocol/Catalog/BuiltIns.cs",
        ];

        string[] forbidden = ["Reflection.Emit", "DynamicMethod", "Expression.Compile", "Assembly.LoadFrom"];
        var root = RepositoryRoot();

        Assert.Multiple(() =>
        {
            foreach (var file in files)
            {
                var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                Assert.That(File.Exists(path), Is.True, $"{file} is not where this test expects it");

                // Skipped on the comment lines that name the APIs in order to say they are not used — the assertion is about code, and a doc comment saying
                // "there is no DynamicMethod here" must not fail it.
                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var api in forbidden)
                    {
                        Assert.That(line, Does.Not.Contain(api), $"{file} names {api}, which #409 rules out on this path");
                    }
                }
            }
        });
    }

    // ── Determinism ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two builds of the same declarations, from two registries, produce the same bytes and the same digest.</summary>
    [Test]
    public void TwoBuildsOfOneDeclarationAreByteIdentical()
    {
        using var dbe = SetupEngine();
        var first = Build(dbe, DeclareAll);
        var second = Build(dbe, DeclareAll);

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexString(second.Utf8), Is.EqualTo(Convert.ToHexString(first.Utf8)));
            Assert.That(second.Hash, Is.EqualTo(first.Hash));
        });
    }

    /// <summary>
    /// Declaration order is not wire order: reversing every declaration, and the creature's fields with them, leaves the bytes untouched.
    /// </summary>
    [Test]
    public void ReorderingDeclarationsChangesNothing()
    {
        using var dbe = SetupEngine();
        var ordered = Build(dbe, DeclareAll);
        var reordered = Build(dbe, DeclareAllReordered);

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexString(reordered.Utf8), Is.EqualTo(Convert.ToHexString(ordered.Utf8)));
            Assert.That(reordered.Hash, Is.EqualTo(ordered.Hash));
        });
    }

    /// <summary>Renaming one field changes the digest, which is what makes the client's skip-on-match safe.</summary>
    [Test]
    public void RenamingAFieldChangesTheHash()
    {
        using var dbe = SetupEngine();
        var before = Build(dbe, DeclareAll);
        var after = Build(dbe, subs =>
        {
            subs.Sessions.Kinds("god", "player");
            subs.Archetype<ProjCreature>(a => a
                .Motion(ProjCreature.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps))
                .OnEnter(ProjCreature.Ai, x => x.Template, Codec.U8, name: "template")
                .Field(ProjCreature.Ai, x => x.Mode, Codec.Enum<ProjAiMode>(bits: 3), name: "mode")
                .Field(ProjCreature.Ai, x => x.Alerted, Codec.Bool, name: "alerted")
                .Field(ProjCreature.Ai, x => x.Level, Codec.U16, name: "rank", group: "vitals")
                .Fraction(ProjCreature.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals"));
            ProjectionTestSchema.DeclarePlayer(subs);
            ProjectionTestSchema.DeclareRock(subs);
            DeclareMessages(subs);
        });

        Assert.That(after.Hash, Is.Not.EqualTo(before.Hash), "'level' became 'rank' and the digest did not move");
    }

    /// <summary>
    /// The committed vector: the bytes this builder produced in an earlier process, byte for byte.
    /// </summary>
    /// <remarks>
    /// <b>This is the cross-process half of foundation/06 § 4 criterion 2.</b> Two builds in one process share every string's identity and the process's hash
    /// seed; a committed artefact does not, so a digest that depended on <c>string.GetHashCode</c>, on a dictionary's enumeration order or on anything else
    /// the runtime randomizes per process fails here and nowhere else. It is also what the TypeScript SDK reads — one set of bytes, two decoders.
    /// </remarks>
    [Test]
    public void TheEmittedCatalogMatchesTheCommittedVector()
    {
        using var dbe = SetupEngine();
        var export = Build(dbe, DeclareAll);

        AssertGolden(export);
    }

    // ── Refusals ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An enum whose values are not 0, 1, 2 … is refused by name at <c>Start</c>: the catalog's contract is that a value's index in the name list IS the
    /// integer that travels (W13), and a gap has no name the wire could carry.
    /// </summary>
    [Test]
    public void AnEnumWithAGapIsRefusedByName()
    {
        using var dbe = SetupEngine();

        var ex = Assert.Throws<InvalidOperationException>(() => Build(dbe, subs => subs.Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjCreature.Ai, x => x.Template, Codec.Enum<SparseMode>(bits: 4), name: "mode"))));

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(nameof(SparseMode)));
            Assert.That(ex.Message, Does.Contain(nameof(SparseMode.Late)), "the refusal names the value that does not line up");
        });
    }

    /// <summary>An event with no routing reaches no session, and the catalog would give it a scope that means nothing.</summary>
    [Test]
    public void AnEventWithNoRoutingIsRefused()
    {
        using var dbe = SetupEngine();

        var ex = Assert.Throws<InvalidOperationException>(() => Build(dbe, subs =>
        {
            var attacks = new EventQueue<SwgAttack>("Attacks", 64);
            subs.Event(attacks, e => e.Field(a => a.Damage, Codec.U16));
        }));

        Assert.That(ex.Message, Does.Contain(nameof(SwgAttack)));
    }

    /// <summary>
    /// A profile asking for a client region in a world with no replicated 2-D position is refused: the region's vertices have nothing to quantize against.
    /// </summary>
    [Test]
    public void AClientRegionWithNoPositionIsRefused()
    {
        using var dbe = SetupEngine();

        var ex = Assert.Throws<InvalidOperationException>(() => Build(dbe, subs =>
            subs.Profile("region", p => p.ClientRegion(maxEdgeM: 512))));

        Assert.That(ex.Message, Does.Contain("region"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CatalogArchetype ArchetypeNamed(Catalog catalog, string name) =>
        Array.Find(catalog.Archetypes, a => a.Name == name) ?? throw new AssertionException($"no archetype named '{name}'");

    private static CatalogMetric MetricNamed(Catalog catalog, string name) =>
        Array.Find(catalog.Metrics, m => m.Name == name) ?? throw new AssertionException($"no metric named '{name}'");

    private static CatalogField FieldNamed(CatalogArchetype archetype, string name) =>
        Array.Find(archetype.Fields, f => f.Name == name) ?? throw new AssertionException($"no field named '{name}'");

    private static string[] Names(CatalogArchetype[] archetypes) => Array.ConvertAll(archetypes, a => a.Name);

    private static string[] Names(CatalogCommand[] commands) => Array.ConvertAll(commands, c => c.Name);

    private static string[] FieldNames(CatalogField[] fields) => Array.ConvertAll(fields, f => f.Name);

    private static void CollectOffsets(CompiledField[] fields, HashSet<double> offsets)
    {
        foreach (var field in fields)
        {
            offsets.Add(field.ComponentOffsetInCluster);
            offsets.Add(field.ComponentSize);
            offsets.Add(field.FieldOffsetInComponent);
            offsets.Add(field.RatioOffsetInComponent);
        }
    }

    private static void WalkProperties(JsonNode node, string path, HashSet<string> allowed, List<string> unexpected)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    // The enums map's keys are declared names, not model properties; its values are checked, its keys are not.
                    if (path != "enums" && !allowed.Contains(pair.Key))
                    {
                        unexpected.Add($"{path}/{pair.Key}");
                    }

                    WalkProperties(pair.Value, path.Length == 0 ? pair.Key : $"{path}/{pair.Key}", allowed, unexpected);
                }

                break;
            case JsonArray array:
                foreach (var element in array)
                {
                    WalkProperties(element, path, allowed, unexpected);
                }

                break;
        }
    }

    private static void CollectNumbers(JsonNode node, List<double> numbers)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    CollectNumbers(pair.Value, numbers);
                }

                break;
            case JsonArray array:
                foreach (var element in array)
                {
                    CollectNumbers(element, numbers);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                numbers.Add(value.GetValue<double>());
                break;
        }
    }

    /// <summary>
    /// Asserts the export equals the committed vector, or rewrites it under <c>TYPHON_UPDATE_GOLDEN=1</c>.
    /// </summary>
    /// <remarks>
    /// The same scheme as <c>Typhon.Protocol.Tests</c>' <c>Golden</c> helper and deliberately not a reference to it: that type is internal to another test
    /// assembly, and the vector has to live in its directory because the TypeScript SDK reads the whole directory from there.
    /// </remarks>
    private static void AssertGolden(CatalogExport export)
    {
        var directory = Path.Combine(RepositoryRoot(), "test", "Typhon.Protocol.Tests", "Golden");
        var binPath = Path.Combine(directory, GoldenCase + ".bin");
        var jsonPath = Path.Combine(directory, GoldenCase + ".json");

        var expectation = new JsonObject
        {
            ["description"] = GoldenDescription,
            ["hash"] = CatalogSerializer.ToHex(export.Hash),
            ["byteCount"] = export.Utf8.Length,
        };

        // "\n", not Environment.NewLine: regenerating on Windows and on Linux must produce the same file.
        var json = expectation.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("TYPHON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(binPath, export.Utf8);
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
            return;
        }

        Assert.That(File.Exists(binPath), Is.True, $"missing golden vector {GoldenCase}.bin - regenerate with TYPHON_UPDATE_GOLDEN=1 and commit it");

        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetString(export.Utf8), Is.EqualTo(Encoding.UTF8.GetString(File.ReadAllBytes(binPath))),
                $"{GoldenCase}.bin drifted from the vector");
            Assert.That(json, Is.EqualTo(File.ReadAllText(jsonPath).ReplaceLineEndings("\n")), $"{GoldenCase}.json drifted from the vector");
        });
    }

    /// <summary>The repository root, found by walking up from the test assembly until the solution file appears.</summary>
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && directory.GetFiles("Typhon.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate the repository root from the test assembly location");
        return directory.FullName;
    }
}

/// <summary>An enum the catalog cannot carry: its values are not the indices of its names, so a client would decode one name as another.</summary>
enum SparseMode : byte
{
    Idle = 0,
    Wander = 1,
    Late = 7,
}
