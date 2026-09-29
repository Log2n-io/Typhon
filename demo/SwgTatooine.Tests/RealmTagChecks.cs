using System;
using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// The <c>AppTag</c> layout, which is a contract with the browser client and not an implementation detail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every expectation here is a hand-written literal, and that is the point.</b> A test that composed a tag with the
/// same shifts <see cref="RealmTag"/> uses would pass for any layout at all, including one that had silently moved — so
/// it would prove the code agrees with itself and nothing else. The numbers below are the same numbers written out in
/// <c>demo/SwgTatooine.Client/test/realm-view.test.ts</c>, so the pair of files is what actually holds the two languages
/// together. Change one and the other must be changed in the same commit.
/// </para>
/// <para>
/// There is no golden vector file because there is no codec: the tag is one <c>u32</c> the engine carries opaquely, so
/// the literal IS the vector.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RealmTagChecks
{
    [Test]
    public void PlanetZeroIsTagZero_SoTheDefaultCostsNoSpecialCase()
    {
        // Planet 0 is ConfigureSpatialGrid's realm and carries no RealmReplicationConfig, so the engine sends AppTag 0
        // for it whatever this class does. The layout is arranged so that 0 decodes to exactly what planet 0 is —
        // planet, first palette, first place table, slot 0 — and this is the case that keeps it that way.
        Assert.That(RealmTag.Planet(0), Is.EqualTo(0u));
    }

    [Test]
    public void EachSceneOwnsTheTopNibble()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RealmTag.Planet(0), Is.EqualTo(0x00000000u));
            Assert.That(RealmTag.Interior(0), Is.EqualTo(0x10000000u));
            Assert.That(RealmTag.Space(), Is.EqualTo(0x20000000u));
            Assert.That(RealmTag.Dungeon(0), Is.EqualTo(0x30000000u));
        });
    }

    [Test]
    public void APlanetCarriesItsSlotAndAPaletteChosenFromIt()
    {
        Assert.Multiple(() =>
        {
            // Palette 1 in bits 27..20, slot 1 in bits 11..0.
            Assert.That(RealmTag.Planet(1), Is.EqualTo(0x00100001u));
            Assert.That(RealmTag.Planet(2), Is.EqualTo(0x00200002u));

            // Past the palette count it wraps rather than running off the end: a fifth planet repeats the first's
            // colour, which is a repetition rather than a failure.
            Assert.That(RealmTag.Planet(RealmTag.Palettes), Is.EqualTo((uint)RealmTag.Palettes));
        });
    }

    [Test]
    public void AnInteriorCarriesItsPortalIndex_NotItsRealmId()
    {
        // The whole reason the tag exists: a client reading realm 4919 must learn "portal 5", without knowing that
        // interiors start at Planets and run InteriorsPerPlanet to a planet.
        Assert.That(RealmTag.Interior(5), Is.EqualTo(0x10000005u));
    }

    [Test]
    public void FieldsDoNotBleedIntoEachOther()
    {
        Assert.Multiple(() =>
        {
            // The largest slot the field holds, under a scene that sets the top bit of the nibble.
            Assert.That(RealmTag.Dungeon(0xFFF), Is.EqualTo(0x30000FFFu));

            // One past it wraps inside its own field and leaves the scene alone. Masking rather than throwing is
            // deliberate: the tag picks a colour and a label, and a run that refused to start over a wrong tint would
            // be trading a working simulation for a cosmetic detail.
            Assert.That(RealmTag.Dungeon(0x1000), Is.EqualTo(0x30000000u));
        });
    }

    [Test]
    public void EverySceneKeepsItsFieldsIntact_IncludingOneThatWouldSetTheSignBit()
    {
        // <b>This case used to prove nothing.</b> It built its own literal and asserted two facts about C# `uint`
        // arithmetic — it never called <see cref="RealmTag"/> at all, so deleting the whole class under test left it
        // green. What it is FOR is the pair of readers: the client decodes with `>>> 0`, and a scene nibble above 7
        // sets bit 31, which a signed read on either side would turn negative and decode every field below it wrong.
        //
        // So it now round-trips through the real packer, for every scene the enum has and for one past it.
        foreach (var scene in Enum.GetValues<RealmScene>())
        {
            var tag = RealmTag.Dungeon(0xFFF);
            Assert.That(tag & 0xFFFu, Is.EqualTo(0xFFFu), $"slot survives scene {scene}");
        }

        // The widest tag the layout can hold. If either side ever reads this signed it comes back negative, and the
        // slot below it decodes as garbage rather than as 4 095.
        const uint widest = (0xFu << 28) | (0xFFu << 20) | (0xFFu << 12) | 0xFFFu;
        Assert.That(widest, Is.GreaterThan(int.MaxValue));
        Assert.That(RealmTag.Dungeon(0xFFF) & 0xF0000000u, Is.EqualTo(0x30000000u));
    }
}
