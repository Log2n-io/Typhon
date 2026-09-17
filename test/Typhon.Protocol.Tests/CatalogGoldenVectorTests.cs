using System;
using System.IO;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// #957 — the repository's first golden vectors, and the pattern the rest of the wire work follows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a committed artefact and not a computed expectation.</b> Every other cross-language check rests on this: the .NET encoder, the TypeScript decoder
/// and the engine round-trip must agree on bytes, and they cannot agree on a value each computes for itself. A committed <c>.bin</c> is the only witness that
/// survives a change to the code that produced it — a self-computed expectation would silently follow the bug.
/// </para>
/// <para>
/// <b>The pattern.</b> Each case is a <c>.bin</c> holding the exact bytes, a <c>.json</c> holding the digest and a one-line description of what the case is
/// for, and regeneration only under <c>TYPHON_UPDATE_GOLDEN=1</c>. Regenerating by default would defeat the whole mechanism: the vectors would rewrite
/// themselves to match whatever the code now does, and the test could never fail.
/// </para>
/// <para>
/// The directory is found by walking up to the project file rather than by copying content to the output directory, because regeneration has to write into
/// the source tree that gets committed, not into <c>bin/</c>.
/// </para>
/// </remarks>
[TestFixture]
public class CatalogGoldenVectorTests
{
    private const string UpdateVariable = "TYPHON_UPDATE_GOLDEN";

    private const string ReferenceDescription = "The worked example from 03-wire-protocol § 4: quantized position with motion, a bit-packed enum, "
        + "an enter-only field, one event, one command, one grid and one metric.";

    /// <summary>The reference catalog's canonical bytes and digest, as committed.</summary>
    [Test]
    public void TheReferenceCatalogMatchesItsGoldenVector()
    {
        AssertGolden("catalog-swg", CatalogSamples.Swg(), ReferenceDescription);
    }

    /// <summary>
    /// The reordered declaration must match the <i>same</i> vector as the reference one. Sharing a vector between two declarations is what makes
    /// order-independence auditable rather than merely asserted — the committed bytes are the witness, not a comparison the code makes with itself.
    /// </summary>
    /// <remarks>
    /// It asserts through the same helper rather than reading the file directly, because reading it directly made this test depend on the sibling that
    /// writes it: NUnit runs this one first by name, so on a regeneration run it failed on a file that did not exist yet.
    /// </remarks>
    [Test]
    public void AReorderedDeclarationMatchesTheSameGoldenVector()
    {
        AssertGolden("catalog-swg", CatalogSamples.SwgReordered(), ReferenceDescription);
    }

    private static void AssertGolden(string caseName, Catalog catalog, string description)
    {
        var directory = GoldenDirectory();
        var binPath = Path.Combine(directory, caseName + ".bin");
        var jsonPath = Path.Combine(directory, caseName + ".json");

        var bytes = CatalogSerializer.ToCanonicalUtf8(catalog);
        var hash = CatalogSerializer.ComputeHash(catalog);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(binPath, bytes);
            var expectation = JsonSerializer.Serialize(
                new GoldenExpectation { Description = description, Hash = hash, ByteCount = bytes.Length },
                GoldenExpectationJson);
            // "\n", not Environment.NewLine: regenerating on Windows and on Linux must produce the same file, or the vectors carry a diff that depends on
            // who last ran the generator rather than on what the catalog says.
            File.WriteAllText(jsonPath, expectation.ReplaceLineEndings("\n") + "\n");
            Assert.Inconclusive($"{UpdateVariable}=1: rewrote {caseName}. Review the diff and commit it; the assertion is skipped on a regeneration run.");
            return;
        }

        Assert.That(File.Exists(binPath), Is.True, $"missing golden vector {caseName}.bin — regenerate with {UpdateVariable}=1 and commit it");

        var committedBytes = File.ReadAllBytes(binPath);
        var committed = JsonSerializer.Deserialize<GoldenExpectation>(File.ReadAllText(jsonPath), GoldenExpectationJson);

        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(Encoding.UTF8.GetString(committedBytes)), "canonical catalog bytes drifted from the vector");
            Assert.That(hash, Is.EqualTo(committed.Hash), "the digest drifted; a client's skip-on-match decision turns on this value");
            Assert.That(bytes.Length, Is.EqualTo(committed.ByteCount));
        });
    }

    private static readonly JsonSerializerOptions GoldenExpectationJson =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string GoldenDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && directory.GetFiles("Typhon.Protocol.Tests.csproj").Length == 0)
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate the test project directory from the test assembly location");
        return Path.Combine(directory.FullName, "Golden");
    }

    private sealed class GoldenExpectation
    {
        public string Description { get; set; }

        public string Hash { get; set; }

        public int ByteCount { get; set; }
    }
}
