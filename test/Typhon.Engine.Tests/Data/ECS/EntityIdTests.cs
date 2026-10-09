using System;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class EntityIdTests
{
    [Test]
    public void Constructor_PacksEntityKeyAndArchetypeId()
    {
        var id = new EntityId(42, 7);
        Assert.That(id.EntityKey, Is.EqualTo(42));
        Assert.That(id.ArchetypeId, Is.EqualTo(7));
    }

    [Test]
    public void Constructor_MaxEntityKey_48Bit()
    {
        long maxKey = (1L << 48) - 1; // 281,474,976,710,655
        var id = new EntityId(maxKey, 0);
        Assert.That(id.EntityKey, Is.EqualTo(maxKey));
        Assert.That(id.ArchetypeId, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_MaxRoutingId_65535()
    {
        // Routing id is now a full 16-bit field (was 12-bit / max 4095).
        var id = new EntityId(1, 65535);
        Assert.That(id.EntityKey, Is.EqualTo(1));
        Assert.That(id.ArchetypeId, Is.EqualTo(65535));
    }

    [Test]
    public void Constructor_RoutingIdAndKey_DoNotBleed()
    {
        // Max key AND max routing id together: neither field must corrupt the other.
        long maxKey = (1L << 48) - 1;
        var id = new EntityId(maxKey, 0xFFFF);
        Assert.That(id.EntityKey, Is.EqualTo(maxKey));
        Assert.That(id.ArchetypeId, Is.EqualTo(0xFFFF));
    }

    [Test]
    public void Constructor_RoutingIdFull16Bits()
    {
        // All 16 low bits are significant now (previously only the low 12 were used).
        var id = new EntityId(100, 0xABCD);
        Assert.That(id.ArchetypeId, Is.EqualTo(0xABCD));
        Assert.That(id.EntityKey, Is.EqualTo(100));
    }

    [Test]
    public void Null_IsDefault()
    {
        Assert.That(EntityId.Null.IsNull, Is.True);
        Assert.That(EntityId.Null.EntityKey, Is.EqualTo(0));
        Assert.That(EntityId.Null.ArchetypeId, Is.EqualTo(0));
    }

    [Test]
    public void IsNull_NonDefault_ReturnsFalse()
    {
        var id = new EntityId(1, 1);
        Assert.That(id.IsNull, Is.False);
    }

    [Test]
    public void Equality_SameValues_Equal()
    {
        var a = new EntityId(42, 7);
        var b = new EntityId(42, 7);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a == b, Is.True);
        Assert.That(a != b, Is.False);
    }

    [Test]
    public void Equality_DifferentKey_NotEqual()
    {
        var a = new EntityId(1, 7);
        var b = new EntityId(2, 7);
        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(a != b, Is.True);
    }

    [Test]
    public void Equality_DifferentArchetype_NotEqual()
    {
        var a = new EntityId(42, 1);
        var b = new EntityId(42, 2);
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void GetHashCode_SameValues_SameHash()
    {
        var a = new EntityId(42, 7);
        var b = new EntityId(42, 7);
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public unsafe void SizeOf_8Bytes()
    {
        Assert.That(sizeof(EntityId), Is.EqualTo(8));
    }

    [Test]
    public void ToString_Null_ShowsNull()
    {
        Assert.That(EntityId.Null.ToString(), Is.EqualTo("Entity(Null)"));
    }

    [Test]
    public void ToString_NonNull_ShowsKeyAndArch()
    {
        var id = new EntityId(42, 7);
        Assert.That(id.ToString(), Does.Contain("42").And.Contain("7"));
    }

    [Test]
    public void RoundTrip_RawValue_Preserves()
    {
        var id = new EntityId(123456789L, 40000); // routing id > 4095 exercises the widened field
        var raw = id.RawValue;
        Assert.That(raw, Is.Not.EqualTo(0UL));

        // Reconstruct via the raw path — 48/16 split.
        var id2 = EntityId.FromRaw((long)raw);
        Assert.That(id2.EntityKey, Is.EqualTo(id.EntityKey));
        Assert.That(id2.ArchetypeId, Is.EqualTo(id.ArchetypeId));
        Assert.That(id2, Is.EqualTo(id));
    }

    [Test]
    public void RoundTrip_ManyValues_Preserves()
    {
        // Sweep keys and routing ids across bit boundaries; every pack/unpack must be lossless and independent.
        foreach (var key in new long[] { 0, 1, 4095, 4096, 65535, 65536, (1L << 24), (1L << 40), (1L << 48) - 1 })
        {
            foreach (var routing in new ushort[] { 0, 1, 4095, 4096, 4097, 32768, 65535 })
            {
                var id = new EntityId(key, routing);
                Assert.That(id.EntityKey, Is.EqualTo(key), $"key {key}, routing {routing}");
                Assert.That(id.ArchetypeId, Is.EqualTo(routing), $"key {key}, routing {routing}");
            }
        }
    }

    // ── ENG-02: the public round trip ───────────────────────────────────────────────────────────────────────────────
    //
    // Before this, an id could be taken apart (EntityKey, ArchetypeId, ToString) and not put back together: FromRaw and
    // RawValue were internal, so an application carrying an id through its own wire format, its account rows, its logs or
    // an admin tool needed a friend declaration or a parallel id scheme. These cases are the contract that fixes it.

    [Test]
    public void FromParts_RebuildsWhatTheAccessorsExpose()
    {
        foreach (var key in new long[] { 0, 1, 65535, 65536, 1L << 24, 1L << 40, (1L << 48) - 1 })
        {
            foreach (var routing in new ushort[] { 0, 1, 4095, 4096, 32768, 65535 })
            {
                var original = new EntityId(key, routing);
                var rebuilt = EntityId.FromParts(original.EntityKey, original.ArchetypeId);
                Assert.That(rebuilt, Is.EqualTo(original), $"key {key}, routing {routing}");
            }
        }
    }

    [Test]
    public void FromRawValue_RebuildsFromRawValue()
    {
        var original = new EntityId(123456789L, 40000);
        Assert.That(EntityId.FromRawValue(original.RawValue), Is.EqualTo(original));
    }

    [Test]
    public void FromRawValue_Zero_IsTheNullEntity()
    {
        Assert.That(EntityId.FromRawValue(0UL), Is.EqualTo(EntityId.Null));
        Assert.That(EntityId.FromRawValue(0UL).IsNull, Is.True);
    }

    /// <summary>
    /// The range check must hold with strict mode OFF, which is the default everywhere and what the NuGet ships. The internal
    /// constructor's equivalent guard sits behind <c>CheckConfig.Enabled</c> and is folded away — correct for a path the engine
    /// feeds itself, wrong for one taking numbers from outside it.
    /// </summary>
    [TestCase(-1L)]
    [TestCase(long.MinValue)]
    [TestCase(1L << 48)]
    [TestCase(long.MaxValue)]
    public void FromParts_AnEntityKeyOutOfRangeIsRefused_WhateverStrictModeSays(long badKey)
    {
        Assume.That(CheckConfig.Enabled, Is.False, "this case is about the guard holding when strict mode is off");
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => EntityId.FromParts(badKey, 7));
        Assert.That(ex.ParamName, Is.EqualTo("entityKey"));
    }

    [Test]
    public void FromParts_TheLargestLegalKeyIsAccepted()
    {
        var id = EntityId.FromParts((1L << 48) - 1, 65535);
        Assert.That(id.EntityKey, Is.EqualTo((1L << 48) - 1));
        Assert.That(id.ArchetypeId, Is.EqualTo(65535));
    }

    [Test]
    public void TryParse_RoundTripsWhatToStringProduces()
    {
        foreach (var id in new[] { EntityId.Null, new EntityId(0, 0), new EntityId(42, 7), new EntityId((1L << 48) - 1, 65535) })
        {
            Assert.That(EntityId.TryParse(id.ToString(), out var parsed), Is.True, id.ToString());
            Assert.That(parsed, Is.EqualTo(id), id.ToString());
        }
    }

    [Test]
    public void TryParse_ToleratesSurroundingWhitespace()
    {
        Assert.That(EntityId.TryParse("  Entity(Key=42, Arch=7)\t", out var id), Is.True);
        Assert.That(id, Is.EqualTo(new EntityId(42, 7)));
    }

    /// <summary>A half-matching line must fail rather than parse its prefix, or a log scraper silently invents ids.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Entity(Null")]
    [TestCase("Entity(Key=42)")]
    [TestCase("Entity(Key=42, Arch=7")]
    [TestCase("Entity(Key=42, Arch=7) trailing")]
    [TestCase("Entity(Key=42,Arch=7)")]               // the space after the comma is part of the form
    [TestCase("Entity(Key=-1, Arch=7)")]              // NumberStyles.None rejects the sign
    [TestCase("Entity(Key=42, Arch=65536)")]          // past a ushort
    [TestCase("Entity(Key=281474976710656, Arch=0)")] // past 48 bits
    [TestCase("Entity(Key=abc, Arch=7)")]
    [TestCase("prefix Entity(Key=42, Arch=7)")]
    public void TryParse_RefusesAnythingElse(string text)
    {
        Assert.That(EntityId.TryParse(text, out var id), Is.False, text ?? "<null>");
        Assert.That(id, Is.EqualTo(EntityId.Null), "a failed parse yields Null, never a partial value");
    }
}
