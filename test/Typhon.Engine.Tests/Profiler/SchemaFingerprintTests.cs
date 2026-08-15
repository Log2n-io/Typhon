using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Feature #614 (F1) AC7 — the schema fingerprint written into every v12 trace header (D-5). Its job is to answer "did the schema move between this capture
/// and the database in front of me?" cheaply, from the header alone, before anyone pays for a sidecar cache or a <c>SchemaHistoryR1</c> walk.
/// </summary>
/// <remarks>
/// Two properties carry the whole value and both are easy to lose by accident: it must be <b>stable</b> (a process-randomised
/// <c>string.GetHashCode</c> would silently report drift on every restart) and <b>order-independent</b> (registration order is not schema, so two processes
/// registering the same components in different orders must agree). A fingerprint that fails either would flag drift that did not happen — and a drift
/// indicator nobody believes is worse than none.
/// </remarks>
[TestFixture]
public sealed class SchemaFingerprintTests
{
    private static ComponentDefinitionRecord Component(string name, int revision) => new() { Name = name, Revision = revision };

    private static ArchetypeDefinitionRecord Archetype(string name, int revision) => new() { Name = name, Revision = revision };

    private static ComponentDefinitionRecord[] Components() => [Component("Game.Position", 3), Component("Game.Health", 1), Component("Game.Inventory", 2)];

    private static ArchetypeDefinitionRecord[] Archetypes() => [Archetype("Unit", 1), Archetype("Building", 4)];

    [Test]
    public void Fingerprint_IsStable_AcrossRepeatedComputation()
    {
        var first = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), Archetypes());
        var second = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), Archetypes());

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Is.Not.Zero, "0 is the 'no engine attached' value — a real schema must never hash to it by accident");
    }

    [Test]
    public void Fingerprint_IsIndependentOfRegistrationOrder()
    {
        var forward = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), Archetypes());

        var reversedComponents = Components();
        System.Array.Reverse(reversedComponents);
        var reversedArchetypes = Archetypes();
        System.Array.Reverse(reversedArchetypes);
        var backward = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(reversedComponents, reversedArchetypes);

        Assert.That(backward, Is.EqualTo(forward), "registration order is not part of the schema — two processes must agree on the same set");
    }

    [Test]
    public void Fingerprint_Changes_WhenAComponentRevisionIsBumped()
    {
        var before = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), Archetypes());

        var bumped = Components();
        bumped[0] = Component("Game.Position", 4);
        var after = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(bumped, Archetypes());

        Assert.That(after, Is.Not.EqualTo(before), "a revision bump is exactly the drift this value exists to detect");
    }

    [Test]
    public void Fingerprint_Changes_WhenAnArchetypeRevisionIsBumped()
    {
        var before = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), Archetypes());

        var bumped = Archetypes();
        bumped[0] = Archetype("Unit", 2);
        var after = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), bumped);

        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void Fingerprint_Changes_WhenAComponentIsAddedOrRemoved()
    {
        var full = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint(Components(), Archetypes());
        var fewer = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint([Component("Game.Position", 3), Component("Game.Health", 1)], Archetypes());

        Assert.That(fewer, Is.Not.EqualTo(full));
    }

    // Without a separator between entries the byte stream "Ab" + rev and "A" + "b…" can coincide. Contrived, but a fingerprint's only job is to be trusted
    // when it says two schemas match, so the cheap collision guard is worth a test that would notice if it were removed.
    [Test]
    public void Fingerprint_DistinguishesSchemasThatConcatenateIdentically()
    {
        var left = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint([Component("AB", 1)], []);
        var right = ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint([Component("A", 1), Component("B", 1)], []);

        Assert.That(right, Is.Not.EqualTo(left));
    }

    [Test]
    public void Fingerprint_OfAnEmptySchema_IsDeterministic()
    {
        Assert.That(ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint([], []),
            Is.EqualTo(ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint([], [])));
    }
}
