using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

/// <summary>The mode a defaulted enum field carries: six names, so the narrowest width that indexes them is three bits.</summary>
enum DefaultedMode : byte
{
    /// <summary>Doing nothing.</summary>
    Idle,

    /// <summary>Walking.</summary>
    Walk,

    /// <summary>Running.</summary>
    Run,

    /// <summary>Fighting.</summary>
    Fight,

    /// <summary>Fleeing.</summary>
    Flee,

    /// <summary>Dead.</summary>
    Dead,
}

/// <summary>Nine names, so three bits no longer index them and a declaration asking for three has to be refused.</summary>
enum WideMode : byte
{
    /// <summary>0.</summary>
    A,

    /// <summary>1.</summary>
    B,

    /// <summary>2.</summary>
    C,

    /// <summary>3.</summary>
    D,

    /// <summary>4.</summary>
    E,

    /// <summary>5.</summary>
    F,

    /// <summary>6.</summary>
    G,

    /// <summary>7.</summary>
    H,

    /// <summary>8.</summary>
    I,
}

/// <summary>
/// A command whose declaration names nothing: every field has to travel under the codec its CLR type implies.
/// </summary>
/// <remarks>
/// No <c>bool</c>, deliberately — <c>CommandRegistry</c> refuses a command struct carrying one, because a <c>bool</c>'s marshalled width differs from its
/// managed width and every offset after it would be measured against a layout that is not the one in memory. The default table still has a row for it, which
/// the event half of this fixture exercises.
/// </remarks>
#pragma warning disable CS0649
struct AllDefaults
{
    /// <summary>i8.</summary>
    public sbyte Tiny;

    /// <summary>u8.</summary>
    public byte Small;

    /// <summary>i16.</summary>
    public short Low;

    /// <summary>u16.</summary>
    public ushort High;

    /// <summary>i32.</summary>
    public int Count;

    /// <summary>u32.</summary>
    public uint Netid;

    /// <summary>f32.</summary>
    public float Speed;

    /// <summary>bits{3}, with the enum attached.</summary>
    public DefaultedMode Mode;
}

/// <summary>A command with a 64-bit field, which has no default: the declaration has to narrow it or ignore it.</summary>
struct HasLong
{
    /// <summary>The field with no default.</summary>
    public long Credits;

    /// <summary>A field that defaults perfectly well.</summary>
    public uint ItemCount;
}

/// <summary>A command with a double field, which has no default quantizer.</summary>
struct HasDouble
{
    /// <summary>The field with no default.</summary>
    public double Angle;
}

/// <summary>A command with a field the declaration wants left off the wire, and a property that is not a field at all.</summary>
struct HasScratch
{
    /// <summary>Travels.</summary>
    public uint Target;

    /// <summary>Does not, once the declaration says so.</summary>
    public int Scratch;

    /// <summary>Computed: a selector resolves it to a name, and there is no storage behind it for the decoder to measure an offset against.</summary>
    public readonly uint Doubled => Target * 2;
}

/// <summary>An event record whose declaration names nothing, including the <c>bool</c> a command struct may not carry.</summary>
struct DefaultedHit
{
    /// <summary>entityRef.</summary>
    public EntityId Victim;

    /// <summary>bool: one bit of the section's pack.</summary>
    public bool Critical;

    /// <summary>u16.</summary>
    public ushort Damage;

    /// <summary>bits{3} with the enum attached.</summary>
    public DefaultedMode Mode;
}

/// <summary>A command carrying an enum whose name list no longer fits three bits.</summary>
struct HasWideEnum
{
    /// <summary>Nine names.</summary>
    public WideMode Mode;
}
#pragma warning restore CS0649

/// <summary>
/// 01-model § 7 — a declared command's or event's fields default to their raw type: the declaration is complete by construction, <c>Field</c> overrides one
/// field's codec, and <c>Ignore</c> is the verb that keeps one off the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> <c>Field</c> used to be the thing that MADE a field travel, so a command declared without listing every member produced a
/// catalog carrying only the listed ones — and the struct behind it arrived zeroed, with nothing but <c>CommandRegistry.UnboundStructFields</c> to say so.
/// Silence has to mean "raw type", not "not replicated", or an omission and a decision are indistinguishable in the declaration and in review.
/// </para>
/// <para>
/// <b>Round trips go through the real ingress path</b>, encoded with <c>Typhon.Protocol</c>'s own writer and decoded by the engine's own binder. A test that
/// spells out wire bytes is green in the same build as a red encoder.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class MessageFieldDefaultsTests : TestBase<MessageFieldDefaultsTests>
{
    // ── the harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ingress path assembled exactly as <c>SubscriptionsRuntime</c> assembles it, over whatever declaration the test hands it.</summary>
    private sealed class Harness : IDisposable
    {
        private long _tick;

        public Harness(Action<SubscriptionsRegistry> declare)
        {
            Resources = new ResourceRegistry(new ResourceRegistryOptions { Name = "MessageFieldDefaultsTests" });
            Allocator = new MemoryAllocator(Resources, new MemoryAllocatorOptions { Name = "MessageFieldDefaultsAllocator" });

            var options = new SubscriptionsOptions { MaxSessions = 16, IngressRingBytes = 4096, IngressPoolBudgetBytes = 1L * 1024 * 1024 };

            Subs = new SubscriptionsRegistry(options);
            Subs.Sessions.Kinds("player");
            Subs.Sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player);
            declare(Subs);
            Subs.Freeze();

            Export = CatalogBuilder.Build(Subs, [], CatalogBuilder.DefaultAppName, appRevision: 0, tickPeriodUs: 10_000, systemNames: []);
            Plan = CatalogPlan.Compile(Export.Canonical);

            Sessions = new SessionTable("Sessions", Resources.Runtime, Allocator, options, Subs.Sessions.SessionEvents);
            CommandTypes = CommandRegistry.Build(Subs, Plan);
            Rings = new IngressRingPool("IngressRings", Resources.Runtime, Allocator, options);
            Ingress = new SubscriptionsIngress(Sessions, Subs, CommandTypes, new CommandTypeBuffers(CommandTypes, options.MaxSessions), Rings,
                options.MaxSessions);
            Api = new SubscriptionsCommands(Ingress);
        }

        public ResourceRegistry Resources { get; }

        public MemoryAllocator Allocator { get; }

        public SubscriptionsRegistry Subs { get; }

        public CatalogExport Export { get; }

        public CatalogPlan Plan { get; }

        public SessionTable Sessions { get; }

        public CommandRegistry CommandTypes { get; }

        public IngressRingPool Rings { get; }

        public SubscriptionsIngress Ingress { get; }

        public SubscriptionsCommands Api { get; }

        public SubscriptionsContext Context { get; } = new();

        public SessionId Admit()
        {
            var request = new AdmissionRequest("player", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "harness");
            Assert.That(Sessions.TryAdmit(Subs.Sessions, request, out var session, out _, out _), Is.True, "the harness could not admit a session");
            return session;
        }

        public void Tick()
        {
            Context.Reset(++_tick, workerCount: 1);
            var chunks = Ingress.BeginTick(Context);
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                Ingress.DrainChunk(chunk, chunks);
            }

            Assert.That(Ingress.DrainFaults, Is.Zero, $"the drain threw: {Ingress.LastDrainFault}");
        }

        public void Dispose()
        {
            Ingress.Dispose();
            Rings.Dispose();
            Sessions.Dispose();
            Allocator.Dispose();
            Resources.Dispose();
        }
    }

    private static byte[] Encode(CatalogPlan plan, string command, ushort seq, RecordValues values)
    {
        var buffer = new byte[4096];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, clientTick: 1, [(plan.CommandByName(command), seq, values)]);
        return writer.Written.ToArray();
    }

    private static string[] FieldNames(CatalogField[] fields)
    {
        var names = new string[fields.Length];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = fields[i].Name;
        }

        return names;
    }

    private static CatalogField FieldNamed(CatalogField[] fields, string name) =>
        Array.Find(fields, f => f.Name == name) ?? throw new AssertionException($"no wire field named '{name}'");

    private static CatalogCommand CommandNamed(Catalog catalog, string name) =>
        Array.Find(catalog.Commands, c => c.Name == name) ?? throw new AssertionException($"no command named '{name}'");

    // ── The default table ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A command declared with no <c>Field</c> call at all: every public instance field reaches the catalog under its CLR type's codec, and a value written
    /// into every one of them comes back through the binder exactly.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is the LAST one. A catalog that merely names eight fields proves the declaration is complete; reading the struct back proves
    /// the binder agrees with the catalog about which byte is which — which is the half that used to be missing, because there was no wire field to disagree
    /// about.
    /// </remarks>
    [Test]
    public void ACommandDeclaringNothing_CarriesEveryFieldUnderItsRawType()
    {
        using var harness = new Harness(static subs => subs.Command<AllDefaults>(c => c.Rate(10_000, 20_000)));

        var command = CommandNamed(harness.Export.Canonical, nameof(AllDefaults));
        Assert.Multiple(() =>
        {
            // Wire order is W11's layout key: one section, the packed field first, then the byte-aligned ones ordinally by name.
            Assert.That(FieldNames(command.Fields), Is.EqualTo(new[] { "Mode", "Count", "High", "Low", "Netid", "Small", "Speed", "Tiny" }),
                "the catalog carries every struct field, in W11 order — packed first, then ordinally by name");

            Assert.That(FieldNamed(command.Fields, "Tiny").Codec.Type, Is.EqualTo("i8"));
            Assert.That(FieldNamed(command.Fields, "Small").Codec.Type, Is.EqualTo("u8"));
            Assert.That(FieldNamed(command.Fields, "Low").Codec.Type, Is.EqualTo("i16"));
            Assert.That(FieldNamed(command.Fields, "High").Codec.Type, Is.EqualTo("u16"));
            Assert.That(FieldNamed(command.Fields, "Count").Codec.Type, Is.EqualTo("i32"));
            Assert.That(FieldNamed(command.Fields, "Netid").Codec.Type, Is.EqualTo("u32"));
            Assert.That(FieldNamed(command.Fields, "Speed").Codec.Type, Is.EqualTo("f32"));

            var mode = FieldNamed(command.Fields, "Mode");
            Assert.That(mode.Codec.Type, Is.EqualTo("bits"), "an enum travels as an integer codec with the value set attached (W13)");
            Assert.That(mode.Codec.N, Is.EqualTo(3), "six names need three bits, and three bits is the narrowest width that indexes them");
            Assert.That(mode.Enum, Is.EqualTo(nameof(DefaultedMode)));
            Assert.That(harness.Export.Canonical.Enums[nameof(DefaultedMode)], Has.Length.EqualTo(6));

            Assert.That(harness.CommandTypes.ByStruct(typeof(AllDefaults)).UnboundStructFields, Is.Empty,
                "a declaration that ignores nothing leaves nothing unbound");
        });

        var session = harness.Admit();
        harness.Ingress.OnCommands(session, Encode(harness.Plan, nameof(AllDefaults), seq: 1, new RecordValues
        {
            ["Tiny"] = FieldValue.Of(-7),
            ["Small"] = FieldValue.Of(200),
            ["Low"] = FieldValue.Of(-3000),
            ["High"] = FieldValue.Of(40_000),
            ["Count"] = FieldValue.Of(-123_456_789),
            ["Netid"] = FieldValue.Of(4_000_000_000d),
            ["Speed"] = FieldValue.Of(12.5),
            ["Mode"] = FieldValue.Of((double)DefaultedMode.Dead),
        }));

        harness.Tick();

        var batch = harness.Api.Commands<AllDefaults>();
        Assert.That(batch.Count, Is.EqualTo(1), "the drain delivered nothing");

        var read = default(AllDefaults);
        foreach (ref readonly var command2 in batch)
        {
            read = command2.Value;
        }

        Assert.Multiple(() =>
        {
            Assert.That(read.Tiny, Is.EqualTo((sbyte)-7));
            Assert.That(read.Small, Is.EqualTo((byte)200));
            Assert.That(read.Low, Is.EqualTo((short)-3000));
            Assert.That(read.High, Is.EqualTo((ushort)40_000));
            Assert.That(read.Count, Is.EqualTo(-123_456_789));
            Assert.That(read.Netid, Is.EqualTo(4_000_000_000u));
            Assert.That(read.Speed, Is.EqualTo(12.5f));
            Assert.That(read.Mode, Is.EqualTo(DefaultedMode.Dead));
        });
    }

    /// <summary>An override changes that field's codec and leaves every other field on its default.</summary>
    [Test]
    public void AnOverride_ChangesOnlyItsOwnField()
    {
        using var harness = new Harness(static subs => subs.Command<AllDefaults>(c => c
            .Rate(10_000, 20_000)
            .Field(m => m.Count, Codec.U8)));

        var command = CommandNamed(harness.Export.Canonical, nameof(AllDefaults));
        Assert.Multiple(() =>
        {
            Assert.That(FieldNames(command.Fields), Is.EqualTo(new[] { "Mode", "Count", "High", "Low", "Netid", "Small", "Speed", "Tiny" }),
                "an override changes a codec, never the field set");
            Assert.That(FieldNamed(command.Fields, "Count").Codec.Type, Is.EqualTo("u8"), "the overridden field takes the declared codec");
            Assert.That(FieldNamed(command.Fields, "Netid").Codec.Type, Is.EqualTo("u32"), "its neighbours keep theirs");
            Assert.That(FieldNamed(command.Fields, "Speed").Codec.Type, Is.EqualTo("f32"));
        });
    }

    /// <summary><c>Ignore</c> keeps a field out of the catalog and out of the binding, and says so where an operator can read it.</summary>
    [Test]
    public void Ignore_LeavesTheFieldOffTheWireAndOutOfTheCatalog()
    {
        using var harness = new Harness(static subs => subs.Command<HasScratch>(c => c.Rate(10_000, 20_000).Ignore(s => s.Scratch)));

        var command = CommandNamed(harness.Export.Canonical, nameof(HasScratch));
        Assert.Multiple(() =>
        {
            Assert.That(FieldNames(command.Fields), Is.EqualTo(new[] { nameof(HasScratch.Target) }), "an ignored field is not in the catalog");
            Assert.That(harness.Subs.Commands[0].IgnoredFields, Is.EqualTo(new[] { nameof(HasScratch.Scratch) }));
            Assert.That(harness.CommandTypes.ByStruct(typeof(HasScratch)).UnboundStructFields, Is.EqualTo(new[] { nameof(HasScratch.Scratch) }),
                "the binder still names it — an operator reads that array to see which members the wire never fills");
        });
    }

    /// <summary>Declaring a codec for an ignored field, or ignoring a declared one, is a contradiction the engine refuses rather than resolves.</summary>
    [Test]
    public void DeclaringAndIgnoringOneField_IsRefused()
    {
        var overrideAfterIgnore = Assert.Throws<InvalidOperationException>(() =>
            new SubscriptionsRegistry().Command<HasScratch>(c => c.Ignore(s => s.Scratch).Field(s => s.Scratch, Codec.U8)));
        var ignoreAfterOverride = Assert.Throws<InvalidOperationException>(() =>
            new SubscriptionsRegistry().Command<HasScratch>(c => c.Field(s => s.Scratch, Codec.U8).Ignore(s => s.Scratch)));

        Assert.Multiple(() =>
        {
            Assert.That(overrideAfterIgnore.Message, Does.Contain(nameof(HasScratch.Scratch)));
            Assert.That(ignoreAfterOverride.Message, Does.Contain(nameof(HasScratch.Scratch)));
        });
    }

    // ── What has no default ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 64-bit field and a <see cref="double"/> field have no default, and the refusal lands on the declaring call naming the field, its type and both ways
    /// out.
    /// </summary>
    /// <remarks>
    /// <b>The call, not <c>Start</c>.</b> The default set cannot be computed when the builder opens, because <c>Field</c> and <c>Ignore</c> run after it — so
    /// the check is the first thing that happens once <c>configure</c> returns. That keeps the stack trace on the line that is wrong instead of the line that
    /// noticed, which is the same reason a duplicate wire name and a ninth change group throw where they are written.
    /// </remarks>
    [Test]
    public void AFieldWithNoDefault_IsRefusedAtTheDeclaringCall()
    {
        var longField = Assert.Throws<InvalidOperationException>(() => new SubscriptionsRegistry().Command<HasLong>(c => c.Rate(4, 8)));
        var doubleField = Assert.Throws<InvalidOperationException>(() => new SubscriptionsRegistry().Command<HasDouble>(_ => { }));

        Assert.Multiple(() =>
        {
            Assert.That(longField.Message, Does.Contain($"{nameof(HasLong)}.{nameof(HasLong.Credits)}"), "the refusal names the field");
            Assert.That(longField.Message, Does.Contain("Int64"), "and its CLR type");
            Assert.That(longField.Message, Does.Contain("Codec.VarUInt.Saturate()"), "and the narrowing that fixes it");
            Assert.That(longField.Message, Does.Contain($".Ignore(x => x.{nameof(HasLong.Credits)})"), "and the other way out");

            Assert.That(doubleField.Message, Does.Contain($"{nameof(HasDouble)}.{nameof(HasDouble.Angle)}"));
            Assert.That(doubleField.Message, Does.Contain("Double"));
            Assert.That(doubleField.Message, Does.Contain("Codec.Quant"), "a double needs a quantizer the declaration chooses");
        });
    }

    /// <summary>Both ways out actually work: narrowing the 64-bit field, and ignoring it.</summary>
    [Test]
    public void A64BitField_TravelsOnceNarrowedOrIgnored()
    {
        using var narrowed = new Harness(static subs => subs.Command<HasLong>(c => c.Field(h => h.Credits, Codec.VarUInt.Saturate())));
        using var ignored = new Harness(static subs => subs.Command<HasLong>(c => c.Ignore(h => h.Credits)));

        Assert.Multiple(() =>
        {
            Assert.That(FieldNames(CommandNamed(narrowed.Export.Canonical, nameof(HasLong)).Fields),
                Is.EqualTo(new[] { nameof(HasLong.Credits), nameof(HasLong.ItemCount) }));
            Assert.That(FieldNames(CommandNamed(ignored.Export.Canonical, nameof(HasLong)).Fields), Is.EqualTo(new[] { nameof(HasLong.ItemCount) }),
                "ignoring the 64-bit field leaves the one that defaults perfectly well");
        });
    }

    /// <summary>A selector naming something that is not a public instance field is refused where it was written, by <c>Field</c> and <c>Ignore</c>.</summary>
    /// <remarks>
    /// A property compiles, and the selector resolves it to a name like any other member access — but there is no storage behind it, so the decoder would
    /// bind a wire field to an offset that does not exist. Catching it here is the difference between a build that fails on the line that is wrong and a
    /// server that refuses to start with a message about a name.
    /// </remarks>
    [Test]
    public void ASelectorNamingNoField_IsRefusedAtTheDeclaringCall()
    {
        var declared = Assert.Throws<InvalidOperationException>(() =>
            new SubscriptionsRegistry().Command<HasScratch>(c => c.Field(s => s.Doubled, Codec.U32)));
        var ignored = Assert.Throws<InvalidOperationException>(() =>
            new SubscriptionsRegistry().Command<HasScratch>(c => c.Ignore(s => s.Doubled)));

        Assert.Multiple(() =>
        {
            Assert.That(declared.Message, Does.Contain($"{nameof(HasScratch)}.{nameof(HasScratch.Doubled)}"));
            Assert.That(declared.Message, Does.Contain("public instance field"));
            Assert.That(ignored.Message, Does.Contain($"{nameof(HasScratch)}.{nameof(HasScratch.Doubled)}"));
        });
    }

    // ── Enums ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A declared width that cannot index the enum's whole name list is refused, at the call that wrote the number.</summary>
    /// <remarks>
    /// Under-width is not a decode failure on either side (W13 lets a value past the end of the list decode as a bare integer), so nothing downstream would
    /// ever notice: it is a set of names the client silently never sees. Refusing it where the author wrote the number is the only place the message can name
    /// both the count and the width.
    /// </remarks>
    [Test]
    public void AnEnumTooWideForItsDeclaredWidth_IsRefused()
    {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Enum<WideMode>(bits: 3));

        Assert.Multiple(() =>
        {
            Assert.That(refusal.Message, Does.Contain(nameof(WideMode)));
            Assert.That(refusal.Message, Does.Contain("4 bits"), "the message says the width to write instead");
        });
    }

    /// <summary>The derived width always holds the whole name list, whatever the count.</summary>
    [Test]
    public void ADefaultedEnum_DerivesAWidthThatHoldsItsNames()
    {
        using var harness = new Harness(static subs => subs.Command<HasWideEnum>(_ => { }));

        var mode = FieldNamed(CommandNamed(harness.Export.Canonical, nameof(HasWideEnum)).Fields, nameof(HasWideEnum.Mode));
        Assert.Multiple(() =>
        {
            Assert.That(mode.Codec.N, Is.EqualTo(4), "nine names do not fit three bits, and the derivation is what stops that being silent");
            Assert.That(mode.Enum, Is.EqualTo(nameof(WideMode)));
            Assert.That(harness.Export.Canonical.Enums[nameof(WideMode)], Has.Length.EqualTo(9));
        });
    }

    // ── Events ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An event's record defaults the same way a command's struct does, <c>bool</c> and <c>EntityId</c> included.</summary>
    [Test]
    public void AnEventDeclaringNothing_CarriesEveryFieldUnderItsRawType()
    {
        var queue = new EventQueue<DefaultedHit>("Hits", 64);
        using var harness = new Harness(subs => subs.Event(queue, e => e.RouteToOwner(h => h.Victim)));

        var declared = harness.Export.Canonical.Events[0];
        Assert.Multiple(() =>
        {
            Assert.That(FieldNames(declared.Fields), Is.EqualTo(new[] { "Critical", "Mode", "Damage", "Victim" }),
                "packed fields first (W11/W12), then the byte-aligned ones ordinally by name");
            Assert.That(FieldNamed(declared.Fields, nameof(DefaultedHit.Critical)).Codec.Type, Is.EqualTo("bool"));
            Assert.That(FieldNamed(declared.Fields, nameof(DefaultedHit.Damage)).Codec.Type, Is.EqualTo("u16"));
            Assert.That(FieldNamed(declared.Fields, nameof(DefaultedHit.Victim)).Codec.Type, Is.EqualTo("entityRef"),
                "an EntityId is a netId on the wire, whether or not the declaration says Entity()");
            Assert.That(FieldNamed(declared.Fields, nameof(DefaultedHit.Mode)).Enum, Is.EqualTo(nameof(DefaultedMode)));
        });
    }

    /// <summary>An event's <c>Ignore</c> keeps a field off the wire while routing still reads it.</summary>
    [Test]
    public void AnEventCanIgnoreAFieldItStillRoutesOn()
    {
        var queue = new EventQueue<DefaultedHit>("Hits", 64);
        using var harness = new Harness(subs => subs.Event(queue, e => e.RouteToOwner(h => h.Victim).Ignore(h => h.Mode)));

        var declared = harness.Export.Canonical.Events[0];
        Assert.Multiple(() =>
        {
            Assert.That(FieldNames(declared.Fields), Is.EqualTo(new[] { "Critical", "Damage", "Victim" }));
            Assert.That(harness.Export.Canonical.Enums, Does.Not.ContainKey(nameof(DefaultedMode)),
                "an enum reaches the catalog because a field named it; ignoring that field takes the value set with it");
        });
    }

    // ── Reproducibility ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One declaration compiles to one catalog: the same bytes and the same digest across two independent builds, defaults included.
    /// </summary>
    /// <remarks>
    /// The risk the defaulting introduces is precisely this one. <see cref="Type.GetFields(System.Reflection.BindingFlags)"/> guarantees no order, so a field
    /// set derived from it could differ run to run — and a catalog hash that moves for no declared reason re-sends the catalog to every connected client.
    /// The defaults are sorted ordinally before they are added, and canonicalization sorts again into W11 order, so neither the list nor the wire can wobble.
    /// </remarks>
    [Test]
    public void TheSameDeclaration_DigestsIdenticallyAcrossTwoBuilds()
    {
        using var first = new Harness(Declare);
        using var second = new Harness(Declare);

        Assert.Multiple(() =>
        {
            Assert.That(second.Export.Utf8, Is.EqualTo(first.Export.Utf8), "the canonical bytes have to be identical");
            Assert.That(second.Export.Hash, Is.EqualTo(first.Export.Hash));
        });

        return;

        static void Declare(SubscriptionsRegistry subs)
        {
            subs.Command<AllDefaults>(c => c.Rate(10, 20).Field(m => m.Count, Codec.U8));
            subs.Command<HasScratch>(c => c.Ignore(s => s.Scratch));
            subs.Event(new EventQueue<DefaultedHit>("Hits", 64), e => e.RouteToOwner(h => h.Victim));
        }
    }

    // ── ctx.Subscriptions ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A system reads this tick's commands through <c>ctx.Subscriptions</c>, inside a live runtime — the shape 01-model § 7 has always shown.
    /// </summary>
    /// <remarks>
    /// <b>Against a live runtime, not a harness.</b> The property is an <c>init</c> member of a struct built at six sites; a harness would assert that the
    /// object exists, which was never in doubt, while the failure worth catching is a dispatch path that forgets to stamp it and hands every system a null.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ASystemReadsCommandsThroughTheTickContext()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var seen = new List<uint>();
        var reached = new List<bool>();
        var gate = new object();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
            schedule.PublicTrack.DeclareDag("Game").CallbackSystem("Input", ctx =>
            {
                lock (gate)
                {
                    reached.Add(ctx.Subscriptions != null);
                }

                if (ctx.Subscriptions == null)
                {
                    return;
                }

                foreach (ref readonly var command in ctx.Subscriptions.Commands<HasScratch>())
                {
                    lock (gate)
                    {
                        seen.Add(command.Value.Target);
                    }
                }
            }), new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200 });

        runtime.Subscriptions.Sessions.Kinds("player");
        runtime.Subscriptions.Sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player);
        runtime.Subscriptions.Command<HasScratch>(c => c.Rate(1000, 2000).Ignore(s => s.Scratch));

        runtime.Start();
        try
        {
            var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
            var request = new AdmissionRequest("player", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "harness");
            Assert.That(subscriptions.Sessions.TryAdmit(subscriptions.Registry.Sessions, request, out var session, out _, out _), Is.True);

            subscriptions.Ingress.OnCommands(session, Encode(subscriptions.CommandTypes.Plan, nameof(HasScratch), seq: 1, new RecordValues
            {
                [nameof(HasScratch.Target)] = FieldValue.Of(77),
            }));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    if (seen.Count > 0)
                    {
                        break;
                    }
                }

                Thread.Sleep(1);
            }

            lock (gate)
            {
                Assert.That(reached, Is.Not.Empty, "the system never ran");
                Assert.That(reached, Has.No.Member(false), "a dispatch path handed a system a TickContext with no Subscriptions on it");
                Assert.That(seen, Is.EqualTo(new List<uint> { 77 }), "ctx.Subscriptions did not deliver the tick's command");
            }
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    /// <summary>A runtime whose application declared no subscriptions hands systems a null, and says so rather than building a table for nobody.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void ARuntimeThatDeclaredNothing_HandsSystemsNoSubscriptions()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var observed = new List<bool>();
        var gate = new object();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
            schedule.PublicTrack.DeclareDag("Game").CallbackSystem("Input", ctx =>
            {
                lock (gate)
                {
                    observed.Add(ctx.Subscriptions == null);
                }
            }), new RuntimeOptions { WorkerCount = 1, BaseTickRate = 200 });

        runtime.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    if (observed.Count > 0)
                    {
                        break;
                    }
                }

                Thread.Sleep(1);
            }

            lock (gate)
            {
                Assert.That(observed, Is.Not.Empty, "the system never ran");
                Assert.That(observed, Has.No.Member(false), "an inactive subscriptions runtime must hand out null, not an object over nothing");
            }
        }
        finally
        {
            runtime.Shutdown();
        }
    }
}
