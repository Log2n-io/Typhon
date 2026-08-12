using NUnit.Framework;

namespace SpaceBattle.Tests;

[TestFixture]
public sealed class CombatRulesTests
{
    [Test]
    public void AdvanceWeaponFire_AtMaximumRange_HitsImmediatelyAndStartsTheFullCooldown()
    {
        WeaponFireResult result = CombatRules.AdvanceWeaponFire(
            cooldownTicksRemaining: 0,
            source: new PositionSnapshot(0f, 0f, 0f),
            target: new PositionSnapshot(BehaviorRules.WeaponRange, 0f, 0f));

        Assert.Multiple(() =>
        {
            Assert.That(result.Attempted, Is.True);
            Assert.That(result.Hit, Is.True);
            Assert.That(result.CooldownTicksRemaining, Is.EqualTo(BehaviorRules.WeaponFireIntervalTicks));
        });
    }

    [Test]
    public void AdvanceWeaponFire_OutsideWeaponRange_ConsumesTheNormalCooldownWithoutDealingDamage()
    {
        WeaponFireResult result = CombatRules.AdvanceWeaponFire(
            cooldownTicksRemaining: 0,
            source: new PositionSnapshot(0f, 0f, 0f),
            target: new PositionSnapshot(BehaviorRules.WeaponRange + 0.001f, 0f, 0f));

        Assert.Multiple(() =>
        {
            Assert.That(result.Attempted, Is.True);
            Assert.That(result.Hit, Is.False);
            Assert.That(result.CooldownTicksRemaining, Is.EqualTo(BehaviorRules.WeaponFireIntervalTicks));
        });
    }

    [Test]
    public void AdvanceWeaponFire_AfterFiftyFutureTicks_FiresAgain()
    {
        WeaponFireResult result = CombatRules.AdvanceWeaponFire(
            cooldownTicksRemaining: 0,
            source: new PositionSnapshot(0f, 0f, 0f),
            target: new PositionSnapshot(BehaviorRules.WeaponRange, 0f, 0f));

        for (int elapsedTicks = 1; elapsedTicks < BehaviorRules.WeaponFireIntervalTicks; elapsedTicks++)
        {
            result = CombatRules.AdvanceWeaponFire(
                result.CooldownTicksRemaining,
                new PositionSnapshot(0f, 0f, 0f),
                new PositionSnapshot(BehaviorRules.WeaponRange, 0f, 0f));
            Assert.That(result.Attempted, Is.False);
        }

        result = CombatRules.AdvanceWeaponFire(
            result.CooldownTicksRemaining,
            new PositionSnapshot(0f, 0f, 0f),
            new PositionSnapshot(BehaviorRules.WeaponRange, 0f, 0f));

        Assert.Multiple(() =>
        {
            Assert.That(result.Attempted, Is.True);
            Assert.That(result.Hit, Is.True);
            Assert.That(result.CooldownTicksRemaining, Is.EqualTo(BehaviorRules.WeaponFireIntervalTicks));
        });
    }

    [Test]
    public void ResolveDamage_MultipleOverkillHits_DestroyTheTargetWithoutUnsignedUnderflow()
    {
        DamageResolution result = DamageResolutionRules.Resolve(
            currentHealth: 300,
            hitCount: 2,
            participatingAttackerCount: 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsDestroyed, Is.True);
            Assert.That(result.RemainingHealth, Is.Zero);
            Assert.That(result.AppliedDamage, Is.EqualTo(300));
            Assert.That(result.ParticipatingAttackerCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CombatReactionDurations_UseTheAgreedFutureTickWindows()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BehaviorRules.EscapingDurationTicks, Is.EqualTo(125));
            Assert.That(BehaviorRules.DisengagingDurationTicks, Is.EqualTo(75));
            Assert.That(BehaviorRules.EscapingSpeed, Is.EqualTo(75f));
            Assert.That(BehaviorRules.DisengagingSpeed, Is.EqualTo(25f));
        });
    }
}
