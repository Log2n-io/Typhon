using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A12's name lookup — the text behind a player's replicated <c>nameId</c> — and what a player's speech is made into before anyone hears it.
/// </summary>
[TestFixture]
internal sealed class PlayerNameChecks
{
    /// <summary>One name is one id, for the life of the process; no name is no id.</summary>
    [Test]
    public void ANameKeepsItsIdAndNoNameIsNone()
    {
        var first = PlayerNames.IdFor("checks_padme");
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Zero);
            Assert.That(PlayerNames.IdFor("checks_padme"), Is.EqualTo(first), "a character logging in again must keep the id clients cached");
            Assert.That(PlayerNames.IdFor("checks_anakin"), Is.Not.EqualTo(first));
            Assert.That(PlayerNames.IdFor(string.Empty), Is.Zero);
            Assert.That(PlayerNames.TryGet(first, out var name) ? name : null, Is.EqualTo("checks_padme"));
        });
    }

    /// <summary>
    /// The lookup answers the ids it knows and leaves out the rest — unknown, malformed, or past the cap — so a client treats an absent id as
    /// "no name" and never as an error.
    /// </summary>
    [Test]
    public void TheLookupAnswersKnownIdsAndLeavesOutTheRest()
    {
        var known = PlayerNames.IdFor("checks_ahsoka");
        var json = PlayerNames.Json($"{known}, 4000000000,x,-1,");
        Assert.That(json, Is.EqualTo($"{{\"{known}\":\"checks_ahsoka\"}}"));
        Assert.That(PlayerNames.Json(null), Is.EqualTo("{}"));
    }

    /// <summary>A request naming more ids than the cap is answered for the first <see cref="PlayerNames.MaxIdsPerRequest"/> only.</summary>
    [Test]
    public void TheLookupIsCapped()
    {
        var known = PlayerNames.IdFor("checks_rex");
        var ids = string.Join(",", System.Linq.Enumerable.Repeat("0", PlayerNames.MaxIdsPerRequest)) + "," + known;
        Assert.That(PlayerNames.Json(ids), Is.EqualTo("{}"));
    }

    /// <summary>
    /// A player's speech keeps at most <see cref="ChatLines.MaxLines"/> lines, with blank ones and control characters gone: 256 new lines were
    /// a bubble taller than the screen, re-sent to everyone in earshot once a second.
    /// </summary>
    [TestCase("hello", "hello")]
    [TestCase("\n\nhello\n\n\nthere\n", "hello\nthere")]
    [TestCase("a\nb\nc\nd\ne\nf", "a\nb\nc\nd e f")]
    [TestCase("tab\there\u0007bell", "tab here bell")]
    [TestCase("caf\u00e9 \U0001F600", "caf\u00e9 \U0001F600")]
    public void SpeechIsMadeFitToShow(string said, string heard)
        => Assert.That(System.Text.Encoding.UTF8.GetString(ChatLines.Normalize(Utf8Text256.From(said)).Utf8), Is.EqualTo(heard));

    /// <summary>Nothing but new lines is nothing at all, which <c>Speak</c> refuses as empty.</summary>
    [Test]
    public void OnlyNewLinesIsEmpty() => Assert.That(ChatLines.Normalize(Utf8Text256.From(new string('\n', 200))).IsEmpty, Is.True);
}
