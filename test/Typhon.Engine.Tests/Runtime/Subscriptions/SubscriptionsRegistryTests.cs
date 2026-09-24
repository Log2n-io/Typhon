using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The SWG Tatooine declaration, as claude/design/SwgTatooine/03-server.md § 5 writes it and 04-protocol.md § 2 tabulates it. It is the acceptance case for
// P1-01: five archetypes, one of them static, one with an owner section, plus a profile, an event and two commands — declared here through the PUBLIC API
// only, with no engine internals named anywhere in this file.
//
// The components are stand-ins for the demo's, carrying the same field shapes: what is being asserted is that a declaration survives the registry intact, not
// that the demo's storage layout is reproduced. The `long Credits` is load-bearing and is not a stand-in — it is the field that must not reach the wire
// without an explicit narrowing.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

enum SwgAiMode : byte
{
    Idle = 0,
    Wander = 1,
    Chase = 2,
    Attack = 3,
    Dead = 4,
}

enum SwgActivity : byte
{
    Standing = 0,
    Walking = 1,
    Fighting = 2,
    Dead = 3,
}

[Component("Typhon.Test.Swg.Bounds", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgBounds
{
    public float X;
    public float Y;
    public float Z;
}

[Component("Typhon.Test.Swg.Struct", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgStruct
{
    public byte Kind;

    /// <summary>Padding: a component's public fields must total at least 8 bytes.</summary>
    public int Reserved;
}

[Component("Typhon.Test.Swg.Spawner", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgSpawner
{
    public byte CreatureTemplate;
    public byte MissionId;

    /// <summary>Padding: a component's public fields must total at least 8 bytes.</summary>
    public int Reserved;
}

[Component("Typhon.Test.Swg.Vitals", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgVitals
{
    public int Health;
    public int MaxHealth;
}

[Component("Typhon.Test.Swg.Ai", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgAi
{
    public byte Template;
    public SwgAiMode Mode;

    /// <summary>Padding: a component's public fields must total at least 8 bytes.</summary>
    public int Reserved;
}

[Component("Typhon.Test.Swg.PlayerState", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgPlayerState
{
    public SwgActivity Activity;
    public byte ControllerKind;

    /// <summary>Padding: a component's public fields must total at least 8 bytes.</summary>
    public int Reserved;
}

[Component("Typhon.Test.Swg.Inventory", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwgInventory
{
    public long Credits;
    public int ItemCount;
}

[Archetype]
class SwgWorldObject : Archetype<SwgWorldObject>
{
    public static readonly Comp<SwgBounds> Bounds = Register<SwgBounds>();
    public static readonly Comp<SwgStruct> Struct = Register<SwgStruct>();
}

[Archetype]
class SwgCreatureLair : Archetype<SwgCreatureLair>
{
    public static readonly Comp<SwgBounds> Bounds = Register<SwgBounds>();
    public static readonly Comp<SwgSpawner> Spawner = Register<SwgSpawner>();
    public static readonly Comp<SwgVitals> Vitals = Register<SwgVitals>();
}

[Archetype]
class SwgCreature : Archetype<SwgCreature>
{
    public static readonly Comp<SwgBounds> Bounds = Register<SwgBounds>();
    public static readonly Comp<SwgAi> Ai = Register<SwgAi>();
    public static readonly Comp<SwgVitals> Vitals = Register<SwgVitals>();
}

[Archetype]
class SwgCityNpc : Archetype<SwgCityNpc>
{
    public static readonly Comp<SwgBounds> Bounds = Register<SwgBounds>();
    public static readonly Comp<SwgAi> Ai = Register<SwgAi>();
}

[Archetype]
class SwgPlayer : Archetype<SwgPlayer>
{
    public static readonly Comp<SwgBounds> Bounds = Register<SwgBounds>();
    public static readonly Comp<SwgPlayerState> State = Register<SwgPlayerState>();
    public static readonly Comp<SwgVitals> Vitals = Register<SwgVitals>();
    public static readonly Comp<SwgInventory> Inventory = Register<SwgInventory>();
}

/// <summary>
/// An archetype with no components, used only to occupy a replication slot. Two type parameters, because 16 marker types then give 256 distinct archetypes
/// without 256 class declarations — which is what the 255-archetype limit has to be pushed past to be tested at all.
/// </summary>
/// <typeparam name="TA">First marker.</typeparam>
/// <typeparam name="TB">Second marker.</typeparam>
class SwgFiller<TA, TB> : Archetype<SwgFiller<TA, TB>>
{
}

// The message structs below are declarations, not values: nothing in this fixture sends one, so every field is read by a selector and written by nobody.
#pragma warning disable CS0649

struct SwgAttack
{
    public EntityId Attacker;
    public EntityId Target;
    public ushort Damage;
}

struct SwgMoveTo
{
    public float X;
    public float Z;
}

struct SwgSetTarget
{
    public uint Target;
}

struct ClientRegion
{
    public float X;
    public float Z;
}

#pragma warning restore CS0649

/// <summary>
/// P1-01 — the registration surface: what an application may declare, what is captured verbatim, and what is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here goes through the public API.</b> That is the slice's acceptance criterion, not an incidental style: the whole point of a declaration
/// surface is that an application never reaches into the engine, so a test that reached into the engine would be testing something no application can do.
/// </para>
/// <para>
/// <b>Two kinds of refusal, tested differently.</b> What one declaration alone decides — a duplicate name, a ninth group, a 256th archetype, an unnarrowed
/// 64-bit source — throws from the declaring call, so those cases need no runtime. What only the whole configuration decides, and what a later phase builds,
/// is refused when the runtime starts, so those cases start one.
/// </para>
/// </remarks>
[TestFixture]
class SubscriptionsRegistryTests : TestBase<SubscriptionsRegistryTests>
{
    private const double MountSpeedMps = 12.0;
    private const double MaxSpeedMps = MountSpeedMps * 1.5;

    /// <summary>The demo's whole wire contract, declared exactly as the design writes it.</summary>
    private static void DeclareTatooine(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god", "player");

        subs.Static<SwgWorldObject>(a => a
            .Position(SwgWorldObject.Bounds)
            .Field(SwgWorldObject.Struct, s => s.Kind, Codec.U8));

        subs.Archetype<SwgCreatureLair>(a => a
            .Motion(SwgCreatureLair.Bounds, m => m.Teleport(MaxSpeedMps))
            .OnEnter(SwgCreatureLair.Spawner, l => l.CreatureTemplate, Codec.U8)
            .Field(SwgCreatureLair.Spawner, l => l.MissionId, Codec.U8)
            .Fraction(SwgCreatureLair.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals"));

        subs.Archetype<SwgCreature>(a => a
            .Motion(SwgCreature.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps))
            .OnEnter(SwgCreature.Ai, b => b.Template, Codec.U8)
            .Field(SwgCreature.Ai, b => b.Mode, Codec.Enum<SwgAiMode>(bits: 3))
            .Fraction(SwgCreature.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals"));

        subs.Archetype<SwgCityNpc>(a => a
            .Motion(SwgCityNpc.Bounds, m => m.Teleport(MaxSpeedMps))
            .Field(SwgCityNpc.Ai, b => b.Mode, Codec.Enum<SwgAiMode>(bits: 3)));

        subs.Archetype<SwgPlayer>(a => a
            .Motion(SwgPlayer.Bounds, m => m.Teleport(MaxSpeedMps))
            .Field(SwgPlayer.State, s => s.Activity, Codec.Enum<SwgActivity>(bits: 3))
            .Field(SwgPlayer.State, s => s.ControllerKind, Codec.U8)
            .Fraction(SwgPlayer.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals")
            .Owner(o => o
                .Field(SwgPlayer.Inventory, i => i.Credits, Codec.VarUInt.Saturate())
                .Field(SwgPlayer.Inventory, i => i.ItemCount, Codec.VarUInt)));

        subs.Profile("god-world", p => p.World()
            .Of<SwgCreature>().Of<SwgCityNpc>().Of<SwgPlayer>().Of<SwgCreatureLair>());

        var attacks = new EventQueue<SwgAttack>("Attacks", 64);
        subs.Event(attacks, e => e
            .RouteToKnown(a => a.Target, a => a.Attacker)
            .Entity(a => a.Attacker)
            .Entity(a => a.Target)
            .Field(a => a.Damage, Codec.U16));

        subs.Command<SwgMoveTo>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(10, burst: 20)
            .Roles(SessionRole.Player)
            .Precheck(static (in SwgMoveTo m) => float.IsFinite(m.X) && float.IsFinite(m.Z)));

        subs.Command<SwgSetTarget>(c => c.Rate(4, burst: 8).Roles(SessionRole.Player, SessionRole.Spectator));

        subs.Metric("swg.creatures.alive", "count", Codec.VarUInt, static () => 0d);
    }

    private static ProjectedField FieldNamed(ArchetypeProjection archetype, string name)
    {
        foreach (var field in archetype.Fields)
        {
            if (field.Name == name)
            {
                return field;
            }
        }

        Assert.Fail($"'{archetype.Name}' has no field named '{name}'.");
        return null;
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SwgBounds>();
        dbe.RegisterComponentFromAccessor<SwgStruct>();
        dbe.RegisterComponentFromAccessor<SwgSpawner>();
        dbe.RegisterComponentFromAccessor<SwgVitals>();
        dbe.RegisterComponentFromAccessor<SwgAi>();
        dbe.RegisterComponentFromAccessor<SwgPlayerState>();
        dbe.RegisterComponentFromAccessor<SwgInventory>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private TyphonRuntime CreateRuntime()
    {
        var options = new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 };
        return TyphonRuntime.Create(SetupEngine(), schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, options);
    }

    /// <summary>
    /// The five-archetype SWG declaration round-trips: every archetype, field, codec, group, owner field and motion setting comes back exactly as it went in.
    /// </summary>
    [Test]
    public void TatooineDeclaration_RoundTripsThroughTheRegistry()
    {
        var subs = new SubscriptionsRegistry();
        DeclareTatooine(subs);

        Assert.That(subs.Archetypes, Has.Count.EqualTo(5));
        Assert.That(subs.Sessions.DeclaredKinds, Is.EqualTo(new[] { "god", "player" }));

        var worldObject = subs.Archetypes[0];
        Assert.Multiple(() =>
        {
            Assert.That(worldObject.Name, Is.EqualTo(nameof(SwgWorldObject)));
            Assert.That(worldObject.IsStatic, Is.True);
            Assert.That(worldObject.Position.IsMotion, Is.False);
            Assert.That(worldObject.Position.ComponentName, Is.EqualTo(nameof(SwgBounds)));
            Assert.That(worldObject.Fields, Has.Count.EqualTo(1));
            Assert.That(worldObject.Fields[0].Name, Is.EqualTo("Kind"));
            Assert.That(worldObject.Fields[0].Codec, Is.EqualTo(Codec.U8));
            Assert.That(worldObject.Fields[0].Group, Is.EqualTo(ArchetypeProjection.DefaultGroup));
        });

        var creature = subs.Archetypes[2];
        var template = FieldNamed(creature, "Template");
        var mode = FieldNamed(creature, "Mode");
        var hp = FieldNamed(creature, "hp");
        Assert.Multiple(() =>
        {
            Assert.That(creature.IsStatic, Is.False);
            Assert.That(creature.Position.IsMotion, Is.True);
            Assert.That(creature.Position.Motion.ToleranceMetres, Is.EqualTo(0.05));
            Assert.That(creature.Position.Motion.TeleportMaxSpeedMps, Is.EqualTo(MaxSpeedMps));

            Assert.That(template.OnEnter, Is.True, "an OnEnter field belongs to no change group");
            Assert.That(template.Group, Is.Null);
            Assert.That(template.ComponentName, Is.EqualTo(nameof(SwgAi)));
            Assert.That(template.SourceFieldName, Is.EqualTo("Template"));

            Assert.That(mode.Codec.Token, Is.EqualTo("bits"));
            Assert.That(mode.Codec.Width, Is.EqualTo(3));
            Assert.That(mode.Codec.EnumName, Is.EqualTo(nameof(SwgAiMode)));
            Assert.That(mode.Group, Is.EqualTo("state"));

            Assert.That(hp.Codec, Is.EqualTo(Codec.Unorm(8)));
            Assert.That(hp.Group, Is.EqualTo("vitals"));
            Assert.That(hp.SourceFieldName, Is.EqualTo("Health"));
            Assert.That(hp.MaxSourceFieldName, Is.EqualTo("MaxHealth"));

            Assert.That(creature.Groups, Is.EqualTo(new[] { "state", "vitals" }));
        });

        var player = subs.Archetypes[4];
        Assert.Multiple(() =>
        {
            Assert.That(player.OwnerFields, Has.Count.EqualTo(2));
            Assert.That(player.OwnerFields[0].Name, Is.EqualTo("Credits"));
            Assert.That(player.OwnerFields[0].Codec.Saturating, Is.True);
            Assert.That(player.OwnerFields[0].Owner, Is.True);
            Assert.That(player.OwnerFields[1].Codec, Is.EqualTo(Codec.VarUInt));
            Assert.That(player.OwnerGroups, Is.EqualTo(new[] { ArchetypeProjection.DefaultOwnerGroup }));
            Assert.That(player.Fields, Has.Count.EqualTo(3), "owner fields live in their own section, not among the public ones");
        });
    }

    /// <summary>The profile, event, commands and metric of the same declaration come back as they went in.</summary>
    [Test]
    public void TatooineDeclaration_CapturesProfilesEventsCommandsAndMetrics()
    {
        var subs = new SubscriptionsRegistry();
        DeclareTatooine(subs);

        var profile = subs.Profiles[0];
        var attack = subs.Events[0];
        var moveTo = subs.Commands[0];
        var setTarget = subs.Commands[1];

        Assert.Multiple(() =>
        {
            Assert.That(profile.Name, Is.EqualTo("god-world"));
            Assert.That(profile.Observers, Has.Count.EqualTo(1));
            Assert.That(profile.Observers[0].Kind, Is.EqualTo(ObserverKind.World));
            Assert.That(profile.Observers[0].Archetypes, Has.Count.EqualTo(4));

            Assert.That(attack.QueueName, Is.EqualTo("Attacks"));
            Assert.That(attack.Routing, Is.EqualTo(EventRouting.ToKnown));
            Assert.That(attack.RoutingEntityFields, Is.EqualTo(new[] { "Target", "Attacker" }));
            Assert.That(attack.Fields, Has.Count.EqualTo(3));
            Assert.That(attack.Fields[0].Codec, Is.EqualTo(Codec.EntityRef));
            Assert.That(attack.Fields[2].Codec, Is.EqualTo(Codec.U16));

            Assert.That(moveTo.Name, Is.EqualTo(nameof(SwgMoveTo)));
            Assert.That(moveTo.Coalesce, Is.EqualTo(CommandCoalesce.LatestPerSession));
            Assert.That(moveTo.RatePerSecond, Is.EqualTo(10));
            Assert.That(moveTo.RateBurst, Is.EqualTo(20));
            Assert.That(moveTo.AllowedRoles, Is.EqualTo(new[] { SessionRole.Player }));
            Assert.That(moveTo.HasPrecheck, Is.True);

            Assert.That(setTarget.Coalesce, Is.EqualTo(CommandCoalesce.Queued));
            Assert.That(setTarget.AllowedRoles, Has.Count.EqualTo(2));

            Assert.That(subs.Metrics, Has.Count.EqualTo(1));
            Assert.That(subs.Metrics[0].Name, Is.EqualTo("swg.creatures.alive"));
            Assert.That(subs.Metrics[0].Unit, Is.EqualTo("count"));
            Assert.That(subs.Metrics[0].Kind, Is.EqualTo(MetricKind.Gauge));
        });
    }

    /// <summary>Two fields cannot share a wire name: the second declaration is refused where it is written.</summary>
    [Test]
    public void DuplicateFieldName_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => subs.Archetype<SwgCreature>(a => a
            .Field(SwgCreature.Ai, b => b.Mode, Codec.Enum<SwgAiMode>(bits: 3))
            .Field(SwgCreature.Ai, b => b.Template, Codec.U8, name: "Mode")));

        Assert.That(ex.Message, Does.Contain("Mode"));
        Assert.That(subs.Archetypes, Is.Empty, "a refused archetype is not half-registered");
    }

    /// <summary>An application command may not take the name of a built-in the engine interprets itself.</summary>
    [Test]
    public void CommandNameCollidingWithABuiltIn_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => subs.Command<ClientRegion>(_ => { }));

        Assert.That(ex.Message, Does.Contain("ClientRegion"));
        Assert.That(subs.Commands, Is.Empty);
    }

    /// <summary>The <c>typhon.</c> prefix names the engine's own metrics; an application metric may not take it.</summary>
    [Test]
    public void MetricUnderTheReservedPrefix_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => subs.Metric("typhon.tick.p50", "ms", Codec.F16, static () => 0d));

        Assert.That(ex.Message, Does.Contain("typhon."));
        Assert.That(subs.Metrics, Is.Empty);
    }

    /// <summary>
    /// No 64-bit integer reaches the wire: a <c>long</c> source needs an explicit narrowing, and gets a message that names the one that exists.
    /// </summary>
    [Test]
    public void A64BitFieldWithoutSaturate_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => subs.Archetype<SwgPlayer>(a => a
            .Owner(o => o.Field(SwgPlayer.Inventory, i => i.Credits, Codec.VarUInt))));

        Assert.That(ex.Message, Does.Contain("Saturate"));
    }

    /// <summary>The same field with the narrowing stated is accepted, and the narrowing is what comes back.</summary>
    [Test]
    public void A64BitFieldWithSaturate_IsAccepted()
    {
        var subs = new SubscriptionsRegistry();

        subs.Archetype<SwgPlayer>(a => a.Owner(o => o.Field(SwgPlayer.Inventory, i => i.Credits, Codec.VarUInt.Saturate())));

        Assert.That(subs.Archetypes[0].OwnerFields[0].Codec.Saturating, Is.True);
        Assert.That(subs.Archetypes[0].OwnerFields[0].Codec.Token, Is.EqualTo("varu"));
    }

    /// <summary>The group mask is a byte, so an archetype gets eight change groups per section and the ninth is refused.</summary>
    [Test]
    public void NinthChangeGroup_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => subs.Archetype<SwgCreature>(a =>
        {
            for (var i = 0; i < 9; i++)
            {
                a.Field(SwgCreature.Ai, b => b.Template, Codec.U8, name: $"f{i}", group: $"g{i}");
            }
        }));

        Assert.That(ex.Message, Does.Contain("9th change group").Or.Contain("g8"));
    }

    /// <summary>A client's entity handle holds the archetype in 8 bits, so 255 archetypes may be replicated and the 256th is refused.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void The256thArchetype_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => DeclareFillerArchetypes(subs));

        Assert.That(subs.Archetypes, Has.Count.EqualTo(255));
        Assert.That(ex.Message, Does.Contain("255"));
    }

    /// <summary>Once the runtime has started, the catalog clients negotiated against is fixed and every further declaration throws.</summary>
    /// <remarks>
    /// It declares the message half of the contract rather than the whole of <c>DeclareTatooine</c> because <c>Start</c> now COMPILES the declarations
    /// (P1-03): this fixture's components are stand-ins that carry no <c>[SpatialIndex]</c> field and its engine configures no spatial grid, so a projected
    /// position has nothing to resolve against and <c>Start</c> refuses it — which is the projection compiler's own test, not this one's. What is under test
    /// here is that the registry freezes, and the registry freezes before anything is compiled.
    /// </remarks>
    [Test]
    public void ConfiguringAfterStart_Throws()
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Sessions.Kinds("god", "player");
        runtime.Subscriptions.Command<SwgSetTarget>(c => c.Rate(4, burst: 8).Roles(SessionRole.Player));
        runtime.Subscriptions.Metric("swg.creatures.alive", "count", Codec.VarUInt, static () => 0d);
        runtime.Start();

        try
        {
            Assert.That(runtime.Subscriptions.IsFrozen, Is.True);
            Assert.Throws<InvalidOperationException>(() => runtime.Subscriptions.Archetype<SwgCityNpc>(_ => { }));
            Assert.Throws<InvalidOperationException>(() => runtime.Subscriptions.Profile("late", p => p.World()));
            Assert.Throws<InvalidOperationException>(() => runtime.Subscriptions.Metric("late", "count", Codec.VarUInt, static () => 0d));
            Assert.Throws<InvalidOperationException>(() => runtime.Subscriptions.Sessions.Kinds("late"));
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    /// <summary>The observer shapes that are still unbuilt are declarable today and refused at <c>Start</c>, naming the shape.</summary>
    [TestCase(ObserverKind.ClientRegion)]
    [TestCase(ObserverKind.Aggregate)]
    public void AnUnbuiltObserver_IsRefusedAtStart(ObserverKind kind)
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Profile("p", p =>
        {
            if (kind == ObserverKind.ClientRegion)
            {
                p.ClientRegion(maxEdgeM: 4096).Of<SwgCreature>();
            }
            else
            {
                p.Aggregate(tileM: 256, rateHz: 1).Of<SwgCreature>();
            }
        });

        var ex = Assert.Throws<NotSupportedException>(runtime.Start);

        Assert.That(ex.Message, Does.Contain("later phase"));
        Assert.That(ex.Message, Does.Contain(kind.ToString()));
    }

    /// <summary>
    /// A <c>Sphere</c> that asks to follow an entity is refused, rather than silently centred somewhere the declaration did not name.
    /// </summary>
    /// <remarks>
    /// The sphere is centred on the session's viewpoint, which an application places each tick. Following an entity means the ENGINE resolving that entity's
    /// position on the replication track, which is separate work — and a refusal is the only honest answer while it is missing, because the alternative is a
    /// declaration whose stated centre is quietly ignored.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void ASphereThatFollowsAnEntityIsRefusedUntilTheEngineSideFollowExists()
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Profile("p", p => p.Sphere(192, leave: 208).AroundControlled().Of<SwgCreature>());

        var ex = Assert.Throws<NotSupportedException>(runtime.Start);
        Assert.That(ex.Message, Does.Contain("viewpoint"));
    }

    /// <summary>A Sphere's leave radius is declarable and refused at <c>Start</c>: an entity is held within one radius, and Phase 2 builds the band.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void ASphereLeaveRadiusIsRefusedUntilHysteresisIsBuilt()
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Profile("p", p => p.Sphere(192, leave: 208).Of<SwgCreature>());

        var ex = Assert.Throws<NotSupportedException>(runtime.Start);
        Assert.That(ex.Message, Does.Contain("leave radius"));
    }

    /// <summary>Two Sphere profiles with different radii are refused: the push index is sized from one radius, and the second would be served at it.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void TwoSphereRadiiAreRefused()
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Profile("near", p => p.Sphere(100).Of<SwgCreature>());
        runtime.Subscriptions.Profile("far", p => p.Sphere(200).Of<SwgCreature>());

        var ex = Assert.Throws<NotSupportedException>(runtime.Start);
        Assert.That(ex.Message, Does.Contain("one radius"));
    }

    /// <summary>A profile with two observers is refused, even of one shape and one radius: it would be a tier, and Phase 2 builds tiers.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AProfileWithTwoObserversIsRefused()
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Profile("p", p =>
        {
            p.Sphere(100).Of<SwgCreature>();
            p.Sphere(100).Of<SwgCreature>();
        });

        var ex = Assert.Throws<NotSupportedException>(runtime.Start);
        Assert.That(ex.Message, Does.Contain("exactly one"));
    }

    /// <summary>A profile that observes nothing starts, and serves its sessions nothing.</summary>
    [Test]
    public void AProfileWithNoObserverStarts()
    {
        using var runtime = CreateRuntime();
        runtime.Subscriptions.Profile("idle", _ => { });

        Assert.DoesNotThrow(runtime.Start);
    }

    /// <summary>A shared source is declarable today and refused at <c>Start</c> as Phase 4 work.</summary>
    [Test]
    public void ASource_IsRefusedAtStartAsPhase4()
    {
        using var runtime = CreateRuntime();
        using var tx = runtime.Engine.CreateQuickTransaction();
        using var view = tx.Query<SwgWorldObject>().ToView();
        runtime.Subscriptions.Source("bazaar", view, _ => { });

        var ex = Assert.Throws<NotSupportedException>(runtime.Start);

        Assert.That(ex.Message, Does.Contain("Phase 4"));
        Assert.That(ex.Message, Does.Contain("bazaar"));
    }

    // 16 markers × 16 markers = 256 archetypes, declared in two levels so the limit can be pushed past without 256 class declarations. Any 16 distinct types
    // will do: a filler archetype has no components and never reaches the schema — it exists only to occupy a replication slot.
    private static void DeclareFillerArchetypes(SubscriptionsRegistry subs)
    {
        DeclareFillerRow<byte>(subs);
        DeclareFillerRow<sbyte>(subs);
        DeclareFillerRow<short>(subs);
        DeclareFillerRow<ushort>(subs);
        DeclareFillerRow<int>(subs);
        DeclareFillerRow<uint>(subs);
        DeclareFillerRow<long>(subs);
        DeclareFillerRow<ulong>(subs);
        DeclareFillerRow<float>(subs);
        DeclareFillerRow<double>(subs);
        DeclareFillerRow<char>(subs);
        DeclareFillerRow<bool>(subs);
        DeclareFillerRow<string>(subs);
        DeclareFillerRow<object>(subs);
        DeclareFillerRow<Guid>(subs);
        DeclareFillerRow<decimal>(subs);
    }

    private static void DeclareFillerRow<TA>(SubscriptionsRegistry subs)
    {
        subs.Archetype<SwgFiller<TA, byte>>(_ => { });
        subs.Archetype<SwgFiller<TA, sbyte>>(_ => { });
        subs.Archetype<SwgFiller<TA, short>>(_ => { });
        subs.Archetype<SwgFiller<TA, ushort>>(_ => { });
        subs.Archetype<SwgFiller<TA, int>>(_ => { });
        subs.Archetype<SwgFiller<TA, uint>>(_ => { });
        subs.Archetype<SwgFiller<TA, long>>(_ => { });
        subs.Archetype<SwgFiller<TA, ulong>>(_ => { });
        subs.Archetype<SwgFiller<TA, float>>(_ => { });
        subs.Archetype<SwgFiller<TA, double>>(_ => { });
        subs.Archetype<SwgFiller<TA, char>>(_ => { });
        subs.Archetype<SwgFiller<TA, bool>>(_ => { });
        subs.Archetype<SwgFiller<TA, string>>(_ => { });
        subs.Archetype<SwgFiller<TA, object>>(_ => { });
        subs.Archetype<SwgFiller<TA, Guid>>(_ => { });
        subs.Archetype<SwgFiller<TA, decimal>>(_ => { });
    }
}
