using System;
using System.Text;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// #957 — the properties the catalog's skip-on-match rests on: a digest that survives declaration order, moves when the contract moves, and never leaks the
/// server's memory layout.
/// </summary>
/// <remarks>
/// <para>
/// A client stores the hash it was sent and offers it back to skip ≈ 5 KB on its next connection; the server decides whether it matches. Every failure mode
/// here is therefore silent by construction — a hash that varies between processes makes the skip never fire, and one that fails to move when a field is
/// renamed makes a client decode the new wire with the old plan.
/// </para>
/// </remarks>
[TestFixture]
public class CatalogCanonicalizationTests
{
    /// <summary>
    /// The trap that motivated the shared helper: <c>string.GetHashCode</c> is randomized per process, so a fingerprint built on it silently differs between
    /// runs. This cannot observe a second process, but it does pin the weaker property that the digest is a pure function of the declaration.
    /// </summary>
    [Test]
    public void TheSameDeclarationAlwaysProducesTheSameBytesAndTheSameHash()
    {
        var first = CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg());
        var second = CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg());

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first), "canonical bytes must be a pure function of the declaration");
            Assert.That(CatalogSerializer.ComputeHash(CatalogSamples.Swg()), Is.EqualTo(CatalogSerializer.ComputeHash(CatalogSamples.Swg())));
        });
    }

    /// <summary>
    /// Declaration order is an authoring detail, not a contract. It must reach neither the digest nor the bytes — and because <c>idx</c> is the wire index,
    /// the only way to have both is to assign it from the sort rather than from the order the application registered things in.
    /// </summary>
    [Test]
    public void ReorderingDeclarationsChangesNeitherTheHashNorTheBytes()
    {
        var declared = CatalogSamples.Swg();
        var reordered = CatalogSamples.SwgReordered();

        Assert.Multiple(() =>
        {
            Assert.That(CatalogSerializer.ComputeHash(reordered), Is.EqualTo(CatalogSerializer.ComputeHash(declared)));
            Assert.That(CatalogSerializer.ToCanonicalUtf8(reordered), Is.EqualTo(CatalogSerializer.ToCanonicalUtf8(declared)));
        });
    }

    /// <summary>The other half of the same rule: a change a client would have to decode differently must move the digest.</summary>
    [Test]
    public void RenamingAFieldChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());

        var renamed = CatalogSamples.Swg();
        renamed.Archetypes[0].Fields[0] = new CatalogField
        {
            Name = renamed.Archetypes[0].Fields[0].Name + "X",
            Codec = renamed.Archetypes[0].Fields[0].Codec,
            Group = renamed.Archetypes[0].Fields[0].Group,
        };

        Assert.That(CatalogSerializer.ComputeHash(renamed), Is.Not.EqualTo(baseline));
    }

    /// <summary>Adding a field is a wire change even when every existing field is untouched, because the record gains a member.</summary>
    [Test]
    public void AddingAFieldChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());

        var extended = CatalogSamples.Swg();
        var creature = extended.Archetypes[0];
        var fields = new CatalogField[creature.Fields.Length + 1];
        Array.Copy(creature.Fields, fields, creature.Fields.Length);
        fields[^1] = new CatalogField { Name = "stamina", Codec = new CatalogCodec { Kind = CodecKind.Unorm, Bits = 8 }, Group = "vitals" };
        extended.Archetypes[0] = new CatalogArchetype { Name = creature.Name, Groups = creature.Groups, Fields = fields };

        Assert.That(CatalogSerializer.ComputeHash(extended), Is.Not.EqualTo(baseline));
    }

    /// <summary>
    /// Changing only a codec parameter — the same field, the same name, one more bit — must move the digest, because a client that decoded it the old way
    /// would read the wrong number of bytes.
    /// </summary>
    [Test]
    public void ChangingACodecParameterChangesTheHash()
    {
        var baseline = CatalogSerializer.ComputeHash(CatalogSamples.Swg());

        var widened = CatalogSamples.Swg();
        var hp = widened.Archetypes[0].Fields[6];
        widened.Archetypes[0].Fields[6] = new CatalogField
        {
            Name = hp.Name,
            Codec = new CatalogCodec { Kind = hp.Codec.Kind, Bits = 16 },
            Group = hp.Group,
            Smoothing = hp.Smoothing,
        };

        Assert.That(CatalogSerializer.ComputeHash(widened), Is.Not.EqualTo(baseline));
    }

    /// <summary>
    /// D2 and D3, asserted by scanning the emitted bytes rather than by inspection: no CLR type name and no storage offset may appear anywhere in a catalog.
    /// </summary>
    /// <remarks>
    /// Scanning is the point. A reviewer reading the model types would conclude the same thing and be right today; this stays right when someone adds a
    /// property later, which is exactly when a layout detail would slip onto the wire and couple it to storage.
    /// </remarks>
    [Test]
    public void TheEmittedCatalogNamesNoClrTypeAndNoStorageOffset()
    {
        var json = Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg()));

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

    /// <summary>Canonicalization must not mutate what the caller handed in: the application keeps its declaration, the engine a canonical copy.</summary>
    [Test]
    public void CanonicalizeDoesNotMutateItsInput()
    {
        var declared = CatalogSamples.Swg();
        var firstArchetypeBefore = declared.Archetypes[0].Name;
        var fieldOrderBefore = declared.Archetypes[0].Fields[0].Name;

        CatalogSerializer.Canonicalize(declared);

        Assert.Multiple(() =>
        {
            Assert.That(declared.Archetypes[0].Name, Is.EqualTo(firstArchetypeBefore));
            Assert.That(declared.Archetypes[0].Fields[0].Name, Is.EqualTo(fieldOrderBefore));
        });
    }

    /// <summary>Wire indices come from the canonical order, densely from zero, so a client can use them to index its decode plan directly.</summary>
    [Test]
    public void WireIndicesAreDenseAndFollowTheCanonicalOrder()
    {
        var canonical = CatalogSerializer.Canonicalize(CatalogSamples.SwgReordered());

        Assert.Multiple(() =>
        {
            for (var i = 0; i < canonical.Archetypes.Length; i++)
            {
                Assert.That(canonical.Archetypes[i].Idx, Is.EqualTo(i));
            }

            Assert.That(canonical.Archetypes[0].Name, Is.EqualTo("Creature"), "ordinal sort puts Creature before Player whatever the declaration said");
            Assert.That(canonical.Archetypes[1].Name, Is.EqualTo("Player"));
        });
    }

    /// <summary>A codec's wire token is the contract the TypeScript table is keyed by, so the tokens are asserted, not assumed.</summary>
    [Test]
    public void CodecKindsSerializeAsTheirWireTokens()
    {
        var json = Encoding.UTF8.GetString(CatalogSerializer.ToCanonicalUtf8(CatalogSamples.Swg()));

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"t\":\"pos2\""));
            Assert.That(json, Does.Contain("\"t\":\"unorm\""), "camelCase of the C# member would have produced 'uNorm'");
            Assert.That(json, Does.Contain("\"t\":\"varu\""), "camelCase of the C# member would have produced 'varU'");
            Assert.That(json, Does.Contain("\"t\":\"tickLo\""));
        });
    }
}
