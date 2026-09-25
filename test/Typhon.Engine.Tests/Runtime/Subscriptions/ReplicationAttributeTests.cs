using NUnit.Framework;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

#pragma warning disable CS0649 // fields read only through the wire

// The attributed twins of SubscriptionsRegistryTests' SwgAttack and SwgMoveTo: the same names in another namespace, so a catalog built from either is
// byte-comparable — a message's catalog name is its type's name.
namespace Typhon.Engine.Tests.Runtime.Attributed
{
    [ReplicatedMessage]
    partial struct SwgAttack
    {
        public EntityId Attacker;
        public EntityId Target;

        [Codec(CodecKind.U16)]
        public ushort Damage;
    }

    [ReplicatedMessage]
    partial struct SwgMoveTo
    {
        [Quant(-8192, 8192, 24)]
        public float X;

        [Quant(-8192, 8192, 24)]
        public float Z;
    }

    /// <summary>A command whose attribute renames and saturates a field, and one field a builder call will override.</summary>
    [ReplicatedMessage]
    partial struct Renamed
    {
        [Codec(CodecKind.U8, Name = "speed", Saturate = true)]
        public ushort Speed;

        [Quant(0, 100, 16)]
        public float Heat;
    }

    /// <summary>A 64-bit field narrowed without saying so, and the same field clamped on purpose.</summary>
    [ReplicatedMessage]
    partial struct Wide
    {
        [Codec(CodecKind.Varu)]
        public long Credits;
    }

    [ReplicatedMessage]
    partial struct WideClamped
    {
        [Codec(CodecKind.Varu, Saturate = true)]
        public long Credits;
    }

    /// <summary>An attribute whose codec the engine's factory refuses: a quantizer of 12 bits.</summary>
    [ReplicatedMessage]
    partial struct BadWidth
    {
        [Quant(0, 1, 12)]
        public float Value;
    }
}

namespace Typhon.Engine.Tests.Runtime
{
    /// <summary>
    /// Replication declared on the data (design/Subscriptions/11 § 5): <c>subs.Archetype&lt;T&gt;()</c> and <c>[ReplicatedMessage]</c> compile to exactly
    /// what the builder calls would — the catalog they produce is the same to the byte — a builder call still wins, and the storage schema never sees the
    /// attributes.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    class ReplicationAttributeTests : TestBase<ReplicationAttributeTests>
    {
        private static CatalogExport Build(DatabaseEngine dbe, Action<SubscriptionsRegistry> declare)
        {
            var subs = new SubscriptionsRegistry();
            subs.Sessions.Kinds("god");
            subs.Profile("god", p => p.World().Of<ProjCreature>().Of<ProjPlayer>());
            declare(subs);
            var plans = ProjectionCompiler.Compile(subs, dbe, ProjectionTestSchema.TickPeriodSeconds, 6);
            return CatalogBuilder.Build(subs, plans, CatalogBuilder.DefaultAppName, appRevision: 0, 100_000, ["Movement"]);
        }

        /// <summary>AC-25's core: the attributed creature and player compile to the catalog DeclareCreature and DeclarePlayer write by hand.</summary>
        [Test]
        public void AnAttributedArchetypeCompilesToItsBuilderDeclaration()
        {
            using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);

            var byBuilder = Build(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                ProjectionTestSchema.DeclarePlayer(subs);
            });
            var byAttributes = Build(dbe, subs =>
            {
                subs.Archetype<ProjCreature>();
                subs.Archetype<ProjPlayer>();
            });

            Assert.Multiple(() =>
            {
                Assert.That(byAttributes.Hash, Is.EqualTo(byBuilder.Hash));
                Assert.That(System.Text.Encoding.UTF8.GetString(byAttributes.Utf8), Is.EqualTo(System.Text.Encoding.UTF8.GetString(byBuilder.Utf8)));
            });
        }

        /// <summary>A builder call for an attributed archetype replaces its attributes entirely: the per-deployment escape.</summary>
        [Test]
        public void ABuilderCallReplacesTheAttributes()
        {
            using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);

            var overridden = Build(dbe, subs =>
            {
                subs.Archetype<ProjCreature>(a => a
                    .Motion(ProjCreature.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
                    .Field(ProjCreature.Ai, x => x.Level, Codec.VarUInt, name: "lvl"));
                subs.Archetype<ProjPlayer>();
            });
            var creature = JsonNode.Parse(overridden.Utf8)["archetypes"].AsArray().First(a => (string)a["name"] == nameof(ProjCreature));
            var names = creature["fields"].AsArray().Select(f => (string)f["name"]).ToArray();

            Assert.That(names, Is.EqualTo(new[] { "lvl" }), "only the builder's field: none of the attributes' survive");
        }

        /// <summary>An attributed command and event compile to the catalog the builder declarations of their plain twins produce.</summary>
        [Test]
        public void AttributedMessagesCompileToTheirBuilderDeclarations()
        {
            using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);

            var byBuilder = Build(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Event<SwgAttack>(e => e.RouteToKnown(a => a.Target, a => a.Attacker).Field(a => a.Damage, Codec.U16));
                subs.Command<SwgMoveTo>(c => c
                    .Coalesce(CommandCoalesce.LatestPerSession)
                    .Rate(10, burst: 20)
                    .Field(m => m.X, Codec.Quant(-8192, 8192, 24))
                    .Field(m => m.Z, Codec.Quant(-8192, 8192, 24)));
            });
            var byAttributes = Build(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Event<Attributed.SwgAttack>(e => e.RouteToKnown(a => a.Target, a => a.Attacker));
                subs.Command<Attributed.SwgMoveTo>(c => c.Coalesce(CommandCoalesce.LatestPerSession).Rate(10, burst: 20));
            });

            Assert.That(System.Text.Encoding.UTF8.GetString(byAttributes.Utf8), Is.EqualTo(System.Text.Encoding.UTF8.GetString(byBuilder.Utf8)));
        }

        /// <summary>
        /// Precedence, field by field: a builder <c>Field</c> beats the attribute, the attribute beats the type's default, and the attribute's name and
        /// saturation reach the catalog.
        /// </summary>
        [Test]
        public void ABuilderFieldBeatsTheAttributeWhichBeatsTheDefault()
        {
            var subs = new SubscriptionsRegistry();
            subs.Command<Attributed.Renamed>(c => c.Rate(10, 20).Field(r => r.Heat, Codec.F16));
            var fields = subs.Commands.Single().Fields;
            var speed = fields.Single(f => f.SourceFieldName == nameof(Attributed.Renamed.Speed));
            var heat = fields.Single(f => f.SourceFieldName == nameof(Attributed.Renamed.Heat));

            Assert.Multiple(() =>
            {
                Assert.That(speed.Name, Is.EqualTo("speed"));
                Assert.That(speed.Codec.Catalog.Kind, Is.EqualTo(CodecKind.U8), "the attribute, not the ushort's default U16");
                Assert.That(speed.Codec.Saturating, Is.True);
                Assert.That(heat.Codec.Catalog.Kind, Is.EqualTo(CodecKind.F16), "the builder's, not the attribute's Quant");
            });
        }

        /// <summary>
        /// An attribute the engine's factory refuses is refused at the declaration, naming the field — the same validation as a builder call.
        /// </summary>
        [Test]
        public void AnInvalidAttributeCodecIsRefusedNamingTheField()
        {
            var subs = new SubscriptionsRegistry();
            var ex = Assert.Throws<InvalidOperationException>(() => subs.Command<Attributed.BadWidth>(c => c.Rate(10, 20)));
            Assert.That(ex.Message, Does.Contain("BadWidth.Value"));
        }

        /// <summary>The builder's 64-bit rule holds for an attribute too: no silent narrowing, a deliberate clamp accepted.</summary>
        [Test]
        public void AnAttributedSixtyFourBitFieldMustSaturate()
        {
            var subs = new SubscriptionsRegistry();
            var ex = Assert.Throws<InvalidOperationException>(() => subs.Command<Attributed.Wide>(c => c.Rate(10, 20)));
            Assert.Multiple(() =>
            {
                Assert.That(ex.Message, Does.Contain("64-bit"));
                Assert.That(() => subs.Command<Attributed.WideClamped>(c => c.Rate(10, 20)), Throws.Nothing);
            });
        }

        /// <summary>
        /// § 5.4 rule 3: the storage schema ignores replication attributes — a codec change never forces a migration. ProjAi carries them; its twin does not;
        /// their generated schemas are field-for-field identical.
        /// </summary>
        [Test]
        public void TheStorageSchemaIgnoresReplicationAttributes()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(typeof(ProjAi), out var attributed), Is.True);
                Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(typeof(ProjAiPlain), out var plain), Is.True);
                Assert.That(Describe(attributed), Is.EqualTo(Describe(plain)));
            });

            static string[] Describe(ComponentSchemaSpec spec)
                => [$"{spec.Revision}/{spec.StorageMode}", .. spec.Fields.Select(f => $"{f.Name}:{f.DotNetType}@{f.Offset}#{f.ExplicitFieldId}:{f.HasIndex}")];
        }
    }

    /// <summary>ProjAi without its replication attributes, for the schema comparison.</summary>
    [Component("Typhon.Test.Proj.AiPlain", 1, StorageMode = StorageMode.SingleVersion)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ProjAiPlain
    {
        [Field]
        public byte Template;

        [Field]
        public ProjAiMode Mode;

        [Field]
        public byte Alerted;

        [Field]
        public ushort Level;

        [Field]
        public int ThinkCooldown;
    }
}
