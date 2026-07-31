using System;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// Feature #614 (F1) AC11 — the v12 capture-identity fields reaching the client. These projections are what let the profiles list render a row (which
/// database, how long, how far behind) from a trace header alone, and what lets the Component Inspector answer "which component moved?".
/// </summary>
/// <remarks>
/// Both projections are shared between the trace and attach runtimes on purpose — a field that reached one session kind but not the other shows up as a panel
/// that works only for recorded captures, which is a confusing bug to chase. These tests exercise the shared implementations directly.
/// </remarks>
[TestFixture]
public sealed class CaptureIdentityProjectionTests
{
    private static TraceFileHeader HeaderWithIdentity(Guid databaseId, string name, bool multiEngine = false)
    {
        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 60f,
            DatabaseId = databaseId,
            TsnMin = 41_022,
            TsnMax = 58_110,
            DurationTicks = 600_000_000,
            TickCount = 3_600,
            SchemaFingerprint = ulong.MaxValue,
        };
        header.SetDatabaseName(name);
        if (multiEngine)
        {
            header.Flags |= (ushort)TraceHeaderFlags.MultipleEnginesObserved;
        }
        return header;
    }

    [Test]
    public void HeaderProjection_CarriesEveryCaptureIdentityField()
    {
        var databaseId = Guid.NewGuid();
        var header = HeaderWithIdentity(databaseId, "world");

        var dto = TraceSessionRuntime.ProjectHeaderDto(in header);

        Assert.Multiple(() =>
        {
            Assert.That(dto.DatabaseId, Is.EqualTo(databaseId.ToString("D")));
            Assert.That(dto.DatabaseName, Is.EqualTo("world"));
            Assert.That(dto.TsnMin, Is.EqualTo(41_022));
            Assert.That(dto.TsnMax, Is.EqualTo(58_110));
            Assert.That(dto.DurationTicks, Is.EqualTo(600_000_000));
            Assert.That(dto.TickCount, Is.EqualTo(3_600));
            Assert.That(dto.MultipleEnginesObserved, Is.False);
            // ulong.MaxValue exceeds JSON's safe integer range, so the fingerprint crosses as a string. Losing its low bits would make two different schemas
            // compare equal — the one failure mode a fingerprint must not have.
            Assert.That(dto.SchemaFingerprint, Is.EqualTo("18446744073709551615"));
        });
    }

    [Test]
    public void HeaderProjection_LeavesIdentityEmpty_ForACaptureWithNoEngine()
    {
        var header = HeaderWithIdentity(Guid.Empty, null);

        var dto = TraceSessionRuntime.ProjectHeaderDto(in header);

        Assert.That(dto.DatabaseId, Is.Empty, "an all-zero GUID is 'no database', not a database whose id happens to be zeros");
        Assert.That(dto.DatabaseName, Is.Empty);
    }

    [Test]
    public void HeaderProjection_SurfacesTheMultiEngineFlag()
    {
        var header = HeaderWithIdentity(Guid.NewGuid(), "world", multiEngine: true);

        Assert.That(TraceSessionRuntime.ProjectHeaderDto(in header).MultipleEnginesObserved, Is.True,
            "the client needs this to know a routing-id bridge is unavailable, rather than silently rendering nothing");
    }

    [Test]
    public void ArchetypeProjection_CarriesTheRoutingIdSeparatelyFromTheCatalogId()
    {
        ArchetypeRecord[] slim =
        [
            new() { ArchetypeId = 1, Name = "Unit", RoutingId = 7 },
            new() { ArchetypeId = 2, Name = "Building", RoutingId = ArchetypeRecord.UnknownRoutingId },
        ];
        ArchetypeDefinitionRecord[] rich =
        [
            new() { ArchetypeId = 1, Name = "Unit", Revision = 3, ComponentTypeIds = [] },
            new() { ArchetypeId = 2, Name = "Building", Revision = 1, ComponentTypeIds = [] },
        ];

        var dtos = TraceSessionRuntime.ProjectArchetypes(slim, rich, []);

        Assert.Multiple(() =>
        {
            Assert.That(dtos[0].ArchetypeId, Is.EqualTo(1));
            Assert.That(dtos[0].RoutingId, Is.EqualTo(7), "catalog id and routing id are different numbers for the same archetype, and both must survive");
            Assert.That(dtos[0].SchemaRevision, Is.EqualTo(3));
            Assert.That(dtos[1].RoutingId, Is.EqualTo(ArchetypeRecord.UnknownRoutingId), "'not recorded' must stay distinguishable from a real id");
        });
    }

    [Test]
    public void ComponentTypeProjection_JoinsTheRevisionFromTheRichDefinitions()
    {
        ComponentTypeRecord[] slim =
        [
            new() { ComponentTypeId = 10, Name = "Game.Position" },
            new() { ComponentTypeId = 11, Name = "Game.Health" },
        ];
        ComponentDefinitionRecord[] rich =
        [
            new() { ComponentTypeId = 10, Name = "Game.Position", Revision = 4 },
            new() { ComponentTypeId = 11, Name = "Game.Health", Revision = 2 },
        ];

        var dtos = TraceSessionRuntime.ProjectComponentTypes(slim, rich);

        Assert.Multiple(() =>
        {
            Assert.That(dtos[0].Revision, Is.EqualTo(4));
            Assert.That(dtos[1].Revision, Is.EqualTo(2));
        });
    }

    [Test]
    public void ComponentTypeProjection_FallsBackToRevisionZero_WhenTheRichTableIsAbsent()
    {
        ComponentTypeRecord[] slim = [new() { ComponentTypeId = 10, Name = "Game.Position" }];

        var dtos = TraceSessionRuntime.ProjectComponentTypes(slim, []);

        Assert.That(dtos[0].Revision, Is.Zero, "a capture with no rich definitions still lists its component types — it just cannot say which revision");
        Assert.That(dtos[0].Name, Is.EqualTo("Game.Position"));
    }
}
