using NUnit.Framework;
using Typhon.Workbench.Security;

namespace Typhon.Workbench.Tests.Security;

[TestFixture]
public sealed class PersonalAccessTokenStoreTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-pat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void Mint_ReturnsHexTokenAndPersistsHash()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        var token = store.Mint("claude-cli");

        Assert.That(token.Length, Is.EqualTo(PersonalAccessTokenStore.TokenHexLength));
        Assert.That(token, Does.Match("^[0-9A-F]+$"), "token must be uppercase hex");

        var path = Path.Combine(_tempDir, "tokens", "claude-cli.token");
        Assert.That(File.Exists(path), "hash file must exist on disk");

        var contents = File.ReadAllText(path);
        Assert.That(contents, Does.Not.Contain(token), "plaintext token must NOT be persisted");
    }

    [Test]
    public void Mint_DuplicateName_Throws()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        store.Mint("dup");
        Assert.Throws<InvalidOperationException>(() => store.Mint("dup"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("has space")]
    [TestCase("../traversal")]
    [TestCase("path/with/slash")]
    [TestCase("dot.dot")]
    public void Mint_InvalidName_Throws(string name)
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        Assert.Throws<ArgumentException>(() => store.Mint(name));
    }

    [Test]
    public void Mint_NameTooLong_Throws()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        var longName = new string('a', 65);
        Assert.Throws<ArgumentException>(() => store.Mint(longName));
    }

    [Test]
    public void Validate_AcceptsPresentedToken()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        var token = store.Mint("ci");
        Assert.That(store.Validate(token), Is.True);
    }

    [Test]
    public void Validate_RejectsRandomToken()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        store.Mint("ci");

        var bogus = new string('A', PersonalAccessTokenStore.TokenHexLength);
        Assert.That(store.Validate(bogus), Is.False);
    }

    [Test]
    public void Validate_RejectsWrongLength()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        store.Mint("ci");
        Assert.That(store.Validate("DEADBEEF"), Is.False);
        Assert.That(store.Validate(""), Is.False);
        Assert.That(store.Validate(null!), Is.False);
    }

    [Test]
    public void Validate_RejectsNonHex()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        store.Mint("ci");
        var notHex = new string('Z', PersonalAccessTokenStore.TokenHexLength);
        Assert.That(store.Validate(notHex), Is.False);
    }

    [Test]
    public void Revoke_RemovesFileAndInvalidatesToken()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        var token = store.Mint("temp");
        Assert.That(store.Validate(token), Is.True);

        var revoked = store.Revoke("temp");
        Assert.That(revoked, Is.True);
        Assert.That(store.Validate(token), Is.False);
        Assert.That(File.Exists(Path.Combine(_tempDir, "tokens", "temp.token")), Is.False);
    }

    [Test]
    public void Revoke_UnknownName_ReturnsFalse()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        Assert.That(store.Revoke("never-existed"), Is.False);
    }

    [Test]
    public void List_ReturnsAllMintedTokens()
    {
        var store = new PersonalAccessTokenStore(_tempDir);
        store.Mint("a-tool");
        store.Mint("b-tool");
        store.Mint("c-tool");

        var list = store.List();
        Assert.That(list.Count, Is.EqualTo(3));
        Assert.That(list[0].Name, Is.EqualTo("a-tool"));
        Assert.That(list[1].Name, Is.EqualTo("b-tool"));
        Assert.That(list[2].Name, Is.EqualTo("c-tool"));
    }

    [Test]
    public void Reload_PicksUpExistingFiles()
    {
        // First store mints a token, second store (simulating a process restart) reloads it from disk
        // and accepts the same plaintext.
        string token;
        {
            var store1 = new PersonalAccessTokenStore(_tempDir);
            token = store1.Mint("persistent");
        }
        {
            var store2 = new PersonalAccessTokenStore(_tempDir);
            Assert.That(store2.Validate(token), Is.True, "token must validate after process restart");
        }
    }

    [Test]
    public void MalformedTokenFile_IsIgnored_DoesNotBreakLoad()
    {
        // Drop a garbage file in the tokens dir; the store must construct cleanly and treat it as
        // absent rather than crashing the host on startup.
        Directory.CreateDirectory(Path.Combine(_tempDir, "tokens"));
        File.WriteAllText(Path.Combine(_tempDir, "tokens", "broken.token"), "{ this is not json");

        Assert.DoesNotThrow(() =>
        {
            var store = new PersonalAccessTokenStore(_tempDir);
            Assert.That(store.List().Count, Is.EqualTo(0));
        });
    }
}
