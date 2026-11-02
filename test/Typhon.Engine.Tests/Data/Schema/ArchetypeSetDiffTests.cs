using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>The pure name map behind archetype-set evolution (#1394, design 07 §5.2): slots matched by component name, never by index.</summary>
class ArchetypeSetDiffTests
{
    private const byte No = ArchetypeSetDiff.NoSlot;

    [Test]
    public void Identical_HasIdentityMaps()
    {
        var d = ArchetypeSetDiff.Compute(["A", "B", "C"], ["A", "B", "C"]);
        Assert.Multiple(() =>
        {
            Assert.That(d.IsIdentical, Is.True);
            Assert.That(d.IsReorderOnly, Is.False);
            Assert.That(d.NewToOld, Is.EqualTo(new byte[] { 0, 1, 2 }));
            Assert.That(d.OldToNew, Is.EqualTo(new byte[] { 0, 1, 2 }));
            Assert.That(d.AddedMask, Is.EqualTo(0));
            Assert.That(d.RemovedOldMask, Is.EqualTo(0));
            Assert.That(d.Describe(), Is.EqualTo("no change"));
        });
    }

    [Test]
    public void AppendAtEnd_MapsAddedToFF()
    {
        var d = ArchetypeSetDiff.Compute(["A", "B"], ["A", "B", "C"]);
        Assert.Multiple(() =>
        {
            Assert.That(d.IsIdentical, Is.False);
            Assert.That(d.IsReorderOnly, Is.False);
            Assert.That(d.NewToOld, Is.EqualTo(new byte[] { 0, 1, No }));
            Assert.That(d.OldToNew, Is.EqualTo(new byte[] { 0, 1 }));
            Assert.That(d.AddedMask, Is.EqualTo(0b100));
            Assert.That(d.RemovedOldMask, Is.EqualTo(0));
            Assert.That(d.HasRemovals, Is.False);
        });
    }

    [Test]
    public void InsertMidList_ShiftsLaterSlots()
    {
        var d = ArchetypeSetDiff.Compute(["A", "B", "C"], ["A", "X", "B", "C"]);
        Assert.Multiple(() =>
        {
            Assert.That(d.NewToOld, Is.EqualTo(new byte[] { 0, No, 1, 2 }));
            Assert.That(d.OldToNew, Is.EqualTo(new byte[] { 0, 2, 3 }));
            Assert.That(d.AddedMask, Is.EqualTo(0b0010));
        });
    }

    [Test]
    public void ParentPrefixGrows_ShiftsChildSlots()
    {
        // Parent {P1} gains P2; the child's own {C1, C2} follow the parent prefix and shift by one.
        var d = ArchetypeSetDiff.Compute(["P1", "C1", "C2"], ["P1", "P2", "C1", "C2"]);
        Assert.Multiple(() =>
        {
            Assert.That(d.NewToOld, Is.EqualTo(new byte[] { 0, No, 1, 2 }));
            Assert.That(d.OldToNew, Is.EqualTo(new byte[] { 0, 2, 3 }));
            Assert.That(d.AddedMask, Is.EqualTo(0b0010));
            Assert.That(d.RemovedOldMask, Is.EqualTo(0));
        });
    }

    [Test]
    public void ReorderOnly_IsReorderOnly()
    {
        var d = ArchetypeSetDiff.Compute(["A", "B", "C"], ["C", "A", "B"]);
        Assert.Multiple(() =>
        {
            Assert.That(d.IsIdentical, Is.False);
            Assert.That(d.IsReorderOnly, Is.True);
            Assert.That(d.NewToOld, Is.EqualTo(new byte[] { 2, 0, 1 }));
            Assert.That(d.OldToNew, Is.EqualTo(new byte[] { 1, 2, 0 }));
            Assert.That(d.AddedMask, Is.EqualTo(0));
            Assert.That(d.RemovedOldMask, Is.EqualTo(0));
        });
    }

    [Test]
    public void SameCountSwap_IsNotIdentical()
    {
        var d = ArchetypeSetDiff.Compute(["A", "B"], ["A", "C"]);
        Assert.Multiple(() =>
        {
            Assert.That(d.IsIdentical, Is.False, "same count, other component: the case a count check missed");
            Assert.That(d.IsReorderOnly, Is.False);
            Assert.That(d.HasRemovals, Is.True);
            Assert.That(d.NewToOld, Is.EqualTo(new byte[] { 0, No }));
            Assert.That(d.OldToNew, Is.EqualTo(new byte[] { 0, No }));
            Assert.That(d.AddedMask, Is.EqualTo(0b10));
            Assert.That(d.RemovedOldMask, Is.EqualTo(0b10));
        });
    }

    [Test]
    public void RemapEnabledBits_DropsRemovedClearsAdded()
    {
        // Persisted {A, B, C, D, E}; runtime {E, A, X, C, D}: B removed, X added, everything else moved.
        string[] persisted = ["A", "B", "C", "D", "E"];
        string[] runtime = ["E", "A", "X", "C", "D"];
        var d = ArchetypeSetDiff.Compute(persisted, runtime);

        for (var mask = 0; mask <= ushort.MaxValue; mask++)
        {
            var expected = 0;
            for (var s = 0; s < runtime.Length; s++)
            {
                var o = Array.IndexOf(persisted, runtime[s]);
                if (o >= 0 && (mask & (1 << o)) != 0)
                {
                    expected |= 1 << s;
                }
            }

            var actual = d.RemapEnabledBits((ushort)mask);
            if (actual != expected)
            {
                Assert.Fail($"mask 0x{mask:X4}: remapped to 0x{actual:X4}, expected 0x{expected:X4}");
            }
            if ((actual & d.AddedMask) != 0)
            {
                Assert.Fail($"mask 0x{mask:X4}: an added slot came out enabled");
            }
        }
    }

    [Test]
    public void DuplicateNameInEitherList_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => ArchetypeSetDiff.Compute(["A", "B", "A"], ["A", "B"]),
                Throws.ArgumentException.With.Property("ParamName").EqualTo("persisted"));
            Assert.That(() => ArchetypeSetDiff.Compute(["A", "B"], ["B", "A", "B"]),
                Throws.ArgumentException.With.Property("ParamName").EqualTo("runtime"));
            Assert.That(ArchetypeSetDiff.FindDuplicate(["A", "B", "C", "B"]), Is.EqualTo(3));
            Assert.That(ArchetypeSetDiff.FindDuplicate(["A", "B", "C"]), Is.EqualTo(-1));
        });
    }

    [Test]
    public void MoreThanSixteenSlots_Throws()
    {
        var seventeen = new string[17];
        for (var i = 0; i < seventeen.Length; i++)
        {
            seventeen[i] = $"C{i}";
        }
        Assert.That(() => ArchetypeSetDiff.Compute(seventeen, ["C0"]), Throws.ArgumentException);
    }

    [Test]
    public void Describe_NamesAddsAndMoves()
    {
        var d = ArchetypeSetDiff.Compute(["Pos", "Old", "Vitals"], ["Pos", "Vitals", "Gear"]);
        Assert.That(d.Describe(), Is.EqualTo("+Gear at slot 2; -Old from slot 1; Vitals 2->1"));
    }
}
