using System;
using System.IO;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// S1 — the asset set is served as static files, and the version the client is told is the <b>immutable</b> one this
/// process pinned at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are for.</b> The server and the client read the same descriptors — the server for collision, portals and
/// spawns, the client to draw them — so the one failure that matters is the two of them disagreeing about which catalogue
/// they are reading. <c>catalogue.json</c> is a mutable pointer, and two readers of a mutable pointer are in step only by
/// luck; what is announced is <c>catalogue.&lt;version&gt;.json</c>, resolved once.
/// </para>
/// <para>
/// Pure parse and resolution checks, plus a temporary folder: no host, no socket, no world.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AssetOriginChecks
{
    private const string Body = """
        {"schema":1,"assets":{"tat_house_small":{"rev":4,"descriptor":"a.json","glb":"a.glb"},
        "shuttle":{"rev":2,"descriptor":"b.json","glb":"b.glb"}},"textures":["t1.ktx2","t2.ktx2","t3.ktx2"],
        "version":"5cc29ba99154"}
        """;

    /// <summary>A catalogue body yields its version and its counts, which is all the server wants from it.</summary>
    [Test]
    public void ACatalogueYieldsItsVersionAndCounts()
    {
        var (version, assets, textures) = AssetOrigin.ReadCatalogue(Body, "catalogue.json");
        Assert.Multiple(() =>
        {
            Assert.That(version, Is.EqualTo("5cc29ba99154"));
            Assert.That(assets, Is.EqualTo(2));
            Assert.That(textures, Is.EqualTo(3));
        });
    }

    /// <summary>Everything a malformed or unknown catalogue can be is refused, with the reason in the message.</summary>
    /// <remarks>
    /// A schema this server does not know is the interesting one: it would parse, and every field it understood would look
    /// right, which is how a client and a server end up drawing two different worlds from one folder.
    /// </remarks>
    [Test]
    public void AnUnreadableCatalogueIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => AssetOrigin.ReadCatalogue("not json", "c.json"), Throws.ArgumentException, "not JSON at all");
            Assert.That(() => AssetOrigin.ReadCatalogue("[1,2]", "c.json"), Throws.ArgumentException, "JSON, but not an object");
            Assert.That(
                () => AssetOrigin.ReadCatalogue("""{"schema":2,"assets":{"a":{}},"version":"ab"}""", "c.json"),
                Throws.ArgumentException,
                "a schema we do not read");
            Assert.That(
                () => AssetOrigin.ReadCatalogue("""{"schema":1,"assets":{"a":{}}}""", "c.json"),
                Throws.ArgumentException,
                "no version");
            Assert.That(
                () => AssetOrigin.ReadCatalogue("""{"schema":1,"assets":{},"version":"ab"}""", "c.json"),
                Throws.ArgumentException,
                "nothing published");
        });
    }

    /// <summary>
    /// A version that is not plain alphanumeric is refused, because it becomes a file name, a URL and a JSON string.
    /// </summary>
    /// <remarks>
    /// The catalogue is a file this process merely found on disk. A version of <c>../../etc/passwd</c> or one carrying a
    /// quote would be an injection point in all three places at once, and the exporter has no reason to emit either.
    /// </remarks>
    [Test]
    public void AVersionThatIsNotANameIsRefused()
    {
        Assert.Multiple(() =>
        {
            foreach (var bad in new[] { "../x", "a/b", "a\"b", "a b", "", new string('f', 65) })
            {
                Assert.That(
                    () => AssetOrigin.ReadCatalogue($"{{\"schema\":1,\"assets\":{{\"a\":{{}}}},\"version\":\"{bad}\"}}", "c.json"),
                    Throws.ArgumentException,
                    $"version '{bad}'");
            }
        });
    }

    /// <summary>A published folder pins the immutable catalogue its pointer names, and announces that name.</summary>
    [Test]
    public void APublishedFolderPinsTheImmutableCatalogue()
    {
        var dir = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, "catalogue.json"), Body);
            File.WriteAllText(Path.Combine(dir, "catalogue.5cc29ba99154.json"), Body);

            var origin = AssetOrigin.Open(dir);
            Assert.Multiple(() =>
            {
                Assert.That(origin.Version, Is.EqualTo("5cc29ba99154"));
                Assert.That(origin.CatalogueName, Is.EqualTo("catalogue.5cc29ba99154.json"), "the immutable name, never the pointer");
                Assert.That(origin.AssetCount, Is.EqualTo(2));
                Assert.That(origin.TextureCount, Is.EqualTo(3));
                // The base, the pinned catalogue, and the table that turns a structure's replicated `asset` index into
                // an asset id — the mapping the server owns because it collides against what it names.
                Assert.That(origin.DemoJson(), Does.StartWith(",\"assets\":{\"base\":\"/assets/\",\"catalogue\":\"catalogue.5cc29ba99154.json\","));
                Assert.That(origin.DemoJson(), Does.Contain("\"structures\":[\"tat_house_large\",\"tat_house_small\""));
                Assert.That(origin.DemoJson(), Does.EndWith("]}"));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A pointer whose immutable twin is absent is refused: the publish wrote the pointer before the catalogue it names.
    /// </summary>
    /// <remarks>
    /// Falling back to the pointer would look like tolerance and would pin the run to a name that can change under it —
    /// the one thing this type exists to prevent.
    /// </remarks>
    [Test]
    public void AnIncompletePublishIsRefused()
    {
        var dir = NewFolder();
        try
        {
            Assert.That(() => AssetOrigin.Open(dir), Throws.ArgumentException, "no pointer: not a published folder");

            File.WriteAllText(Path.Combine(dir, "catalogue.json"), Body);
            Assert.That(() => AssetOrigin.Open(dir), Throws.ArgumentException, "a pointer with no immutable twin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A directory that is not there is refused by name, rather than serving nothing quietly.</summary>
    [Test]
    public void AMissingDirectoryIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => AssetOrigin.Open(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}")), Throws.ArgumentException);
            // ArgumentNullException for null, which is what ThrowIfNullOrWhiteSpace raises and is a kind of ArgumentException.
            Assert.That(() => AssetOrigin.Open(null), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AssetOrigin.Open("   "), Throws.ArgumentException);
        });
    }

    /// <summary>
    /// The brotli sidecar is served when the caller offers <c>br</c> as a token, and not when the token merely appears.
    /// </summary>
    /// <remarks>
    /// A substring test would match <c>brotli</c> and, worse, would honour <c>br;q=0</c> — serving precompressed bytes to a
    /// client that said it cannot decode them, which is unreadable rather than slow.
    /// </remarks>
    [Test]
    public void TheSidecarFollowsTheAcceptEncodingToken()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AssetOrigin.AcceptsBrotli("br"), Is.True);
            Assert.That(AssetOrigin.AcceptsBrotli("gzip, deflate, br"), Is.True);
            Assert.That(AssetOrigin.AcceptsBrotli(" BR "), Is.True, "the token is case-insensitive");
            Assert.That(AssetOrigin.AcceptsBrotli("br;q=0.5"), Is.True, "a quality that is not zero");
            Assert.That(AssetOrigin.AcceptsBrotli("br;q=0"), Is.False, "an explicit refusal");
            Assert.That(AssetOrigin.AcceptsBrotli("gzip, br;q=0.0"), Is.False);
            Assert.That(AssetOrigin.AcceptsBrotli("gzip"), Is.False);
            Assert.That(AssetOrigin.AcceptsBrotli("brotli"), Is.False, "not the token, whatever it contains");
            Assert.That(AssetOrigin.AcceptsBrotli(""), Is.False);
            Assert.That(AssetOrigin.AcceptsBrotli(null), Is.False);
        });
    }

    /// <summary>
    /// <c>--assets</c> is a hosting flag: refused without <c>--serve</c>, and refused when the folder is not there.
    /// </summary>
    [Test]
    public void TheFlagIsRefusedWithoutAHostOrAFolder()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}");
        Assert.Multiple(() =>
        {
            Assert.That(() => CommandLine.Parse(["--assets", absent]), Throws.ArgumentException, "no web host to serve it from");
            Assert.That(() => CommandLine.Parse(["--serve", "8080", "--assets", absent]), Throws.ArgumentException, "the folder is not there");
            Assert.That(CommandLine.Parse(["--serve", "8080"]).AssetDirectory, Is.Null, "and nothing to say means no assets");
        });
    }

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
