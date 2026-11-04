using System;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The combat rolls (crafting-catalog.md § 9.3) through the drain's own <see cref="SimBridge.RollBlow(int, EntityId, EntityId, long, int, uint, out bool)"/>, seeded <c>hash(attacker, target, tick)</c>:
/// C1-3's distribution, a fight replaying to the same numbers, armour applied after the crit, and C1-1's kill time for the standard blaster.
/// </summary>
[TestFixture]
internal sealed class CombatRollChecks
{
    // The drain's roll at a fixed seed salt: no served world is needed for it.
    private static int Roll(int damage, EntityId attacker, EntityId target, long tick, int mitigation, out bool crit)
        => SimBridge.RollBlow(damage, attacker, target, tick, mitigation, 0x5EEDu, out crit);

    private static EntityId Id(ulong key) => EntityId.FromRawValue((key << 16) | 7);

    /// <summary>C1-3: 10 000 seeded blows average damage × (1 + c(m − 1)) within 1 %, 5 % ± 0.5 % are crits, and every one lies in its band.</summary>
    [Test]
    public void TenThousandSeededBlows_HaveTheCataloguesMeanAndCritRate()
    {
        const int n = 10_000;
        const int damage = 1_000;
        long sum = 0;
        var crits = 0;
        for (var i = 0; i < n; i++)
        {
            var hit = Roll(damage, Id((ulong)(1 + (i % 97))), Id((ulong)(1_000 + (i / 97))), 50_000 + i, 0, out var crit);
            crits += crit ? 1 : 0;
            sum += hit;
            var lo = crit ? damage * 0.9 * 1.5 : damage * 0.9;
            Assert.That(hit, Is.InRange((int)Math.Floor(lo), (int)Math.Ceiling(lo / 0.9 * 1.1)), $"blow {i} out of its band");
        }

        var expected = damage * (1 + (Equipment.CritChance * (Equipment.CritMultiplier - 1)));
        Assert.Multiple(() =>
        {
            Assert.That((double)sum / n, Is.EqualTo(expected).Within(expected * 0.01));
            Assert.That((double)crits / n, Is.EqualTo(Equipment.CritChance).Within(0.005));
        });
    }

    /// <summary>A fight replays: the same attacker, target and tick roll the same blow; armour applies after the crit, and a blow never falls below 1.</summary>
    [Test]
    public void ABlow_IsReproducible_AndArmourAppliesAfterTheCrit()
    {
        var a = Roll(500, Id(11), Id(22), 777, 0, out var critA);
        var b = Roll(500, Id(11), Id(22), 777, 0, out var critB);
        var armoured = Roll(500, Id(11), Id(22), 777, 400, out var critC);
        Assert.Multiple(() =>
        {
            Assert.That((b, critB), Is.EqualTo((a, critA)), "the same blow rolled twice differs");
            Assert.That(critC, Is.EqualTo(critA), "armour changed the crit roll");
            Assert.That(armoured, Is.EqualTo(Equipment.Mitigate(a, 400)), "armour is not applied to the rolled blow");
            Assert.That(Roll(1, Id(1), Id(2), 3, 750, out _), Is.EqualTo(1), "the floor");
        });
    }

    /// <summary>
    /// C1-1, on the drain's numbers: the standard blaster, on level against a CL 1 beast, kills in 8 s on average over 200 fights — every blow its delay
    /// apart, rolled — within 10 %, as § 14.2 promises. The level factor is ×1 on level.
    /// </summary>
    [Test]
    public void TheStandardBlaster_KillsAnOnLevelBeast_InEightSeconds()
    {
        Equipment.TryStandard(ItemCatalog.Blaster, out var blaster);
        var health = ContentLevels.CreatureHealth(1, foe: false);
        double total = 0;
        for (var fight = 0; fight < 200; fight++)
        {
            var hp = health;
            var blows = 0;
            while (hp > 0)
            {
                hp -= Roll(blaster.Stat0, Id((ulong)(10 + fight)), Id((ulong)(5_000 + fight)), (fight * 1_000L) + blows, 0, out _);
                blows++;
            }

            // The first blow lands at once, each next one a delay later: the time is the delays between the first and the killing blow.
            total += (blows - 1) * blaster.Stat1 / 1000.0;
        }

        var mean = total / 200;
        TestContext.Progress.WriteLine($"standard blaster vs a CL 1 beast: {mean:F2} s to kill on average over 200 fights");
        Assert.That(mean, Is.EqualTo(8.0).Within(0.8));
    }
}
