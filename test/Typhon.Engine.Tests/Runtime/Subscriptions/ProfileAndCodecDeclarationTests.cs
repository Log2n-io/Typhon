using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>Five names, so <c>bits{2}</c> is one short of carrying it and <c>bits{3}</c> is the smallest that does.</summary>
enum DeclStance : byte
{
    Standing = 0,
    Crouched = 1,
    Prone = 2,
    Sitting = 3,
    Mounted = 4,
}

/// <summary>Two names, the smallest value set that needs a bit at all.</summary>
enum DeclSide : byte
{
    Rebel = 0,
    Imperial = 1,
}

/// <summary>
/// P1-02 — the two declaration-time refusals a projection depends on: an <c>enum</c> too wide for its codec (W13), and a profile holding more observers than
/// it declared room for.
/// </summary>
/// <remarks>
/// Both are checked where the author wrote the number, which is the whole point. W13 lets a value past the end of the name list decode as a bare integer, so
/// an under-wide <c>enum</c> fails on neither side of the wire — it is simply a set of names no client ever sees. The observer cap is the mirror image: it
/// used to be checked against a constant nobody could raise, so a legal declaration was refused instead of an illegal one being caught.
/// </remarks>
[TestFixture]
class ProfileAndCodecDeclarationTests
{
    /// <summary>An enum whose names fit the declared width is accepted, and carries the type the catalog exports.</summary>
    [Test]
    public void EnumWithinItsWidth_IsAccepted()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Codec.Enum<DeclStance>(bits: 3).EnumName, Is.EqualTo(nameof(DeclStance)), "5 names need 3 bits");
            Assert.That(Codec.Enum<DeclStance>(bits: 8).EnumName, Is.EqualTo(nameof(DeclStance)), "a wider codec is legal; W13 bounds it from below only");
            Assert.That(Codec.Enum<DeclSide>(bits: 1).EnumName, Is.EqualTo(nameof(DeclSide)), "2 names fit exactly one bit");
            Assert.That(Codec.Enum<DeclStance>(bits: 3).Width, Is.EqualTo(3), "an enum is an attribute on a bits codec, not a codec of its own");
        });
    }

    /// <summary>
    /// An enum with more names than the declared width indexes is refused at the declaring call, naming the enum, its count and the width that would work.
    /// </summary>
    [Test]
    public void EnumWiderThanItsCodec_IsRefusedByName()
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Enum<DeclStance>(bits: 2));

        Assert.Multiple(() =>
        {
            Assert.That(thrown.Message, Does.Contain(nameof(DeclStance)), "the message names the enum");
            Assert.That(thrown.Message, Does.Contain("5"), "and how many names it has");
            Assert.That(thrown.Message, Does.Contain("4"), "and what 2 bits actually index");
            Assert.That(thrown.Message, Does.Contain("3 bits"), "and the width that would carry it");
        });

        // One bit indexes two values, and the 5-name enum needs three. The boundary is the interesting case: |names| <= 2^n, not < 2^n.
        Assert.DoesNotThrow(() => Codec.Enum<DeclSide>(bits: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Enum<DeclStance>(bits: 1));
    }

    /// <summary>A profile holds as many observers as it declared room for, and the default is four.</summary>
    [Test]
    public void ProfileCapsObserversAtItsOwnCeiling()
    {
        var subs = new SubscriptionsRegistry();
        subs.Profile("default", p =>
        {
            p.World();
            p.Sphere(64);
            p.Sphere(128);
            p.Sphere(256);
        });

        var profile = subs.Profiles[0];
        Assert.That(profile.MaxObservers, Is.EqualTo(SessionLimits.DefaultMaxObservers));
        Assert.That(profile.Observers, Has.Count.EqualTo(4));
    }

    /// <summary>
    /// A profile that raises its own ceiling may declare that many observers — the bug being that the cap used to be a constant no application could raise.
    /// </summary>
    /// <remarks>
    /// A session admitted with <c>SessionLimits { MaxObservers = 6 }</c> could never bind a six-observer profile, because the profile could not be written:
    /// the declaration was capped at <see cref="SessionLimits.DefaultMaxObservers"/> rather than at its own limit. The session's limit is admission's to
    /// enforce; the declaration's is the declaration's.
    /// </remarks>
    [Test]
    public void ProfileMayRaiseItsCeilingAboveTheDefault()
    {
        var subs = new SubscriptionsRegistry();
        subs.Profile("wide", p =>
        {
            p.MaxObservers(6);
            p.World();
            p.Sphere(32);
            p.Sphere(64);
            p.Sphere(128);
            p.Sphere(256);
            p.Aggregate(512, 1);
        });

        var profile = subs.Profiles[0];
        Assert.Multiple(() =>
        {
            Assert.That(profile.MaxObservers, Is.EqualTo(6));
            Assert.That(profile.Observers, Has.Count.EqualTo(6), "six observers, which the default cap refused outright");
            Assert.That(SessionLimits.DefaultMaxObservers, Is.EqualTo(4), "and the default is still four, for a profile that says nothing");
        });
    }

    /// <summary>Past its own ceiling the profile is refused, and the message says how to raise it and who enforces a session's own limit.</summary>
    [Test]
    public void ProfilePastItsCeiling_IsRefusedWithTheWayOut()
    {
        var subs = new SubscriptionsRegistry();
        var thrown = Assert.Throws<InvalidOperationException>(() => subs.Profile("over", p =>
        {
            p.MaxObservers(2);
            p.World();
            p.Sphere(64);
            p.Sphere(128);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(thrown.Message, Does.Contain("over"), "the message names the profile");
            Assert.That(thrown.Message, Does.Contain("MaxObservers"), "and the call that raises the ceiling");
            Assert.That(thrown.Message, Does.Contain("admission"), "and where a session's own limit is enforced");
        });
    }

    /// <summary>The ceiling is set before the observers, and lowering it under what is already declared is a declaration error.</summary>
    [Test]
    public void LoweringTheCeilingBelowWhatIsDeclared_IsRefused()
    {
        var subs = new SubscriptionsRegistry();

        Assert.Throws<InvalidOperationException>(() => subs.Profile("late", p =>
        {
            p.World();
            p.Sphere(64);
            p.MaxObservers(1);
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => subs.Profile("zero", p => p.MaxObservers(0)));
    }
}
